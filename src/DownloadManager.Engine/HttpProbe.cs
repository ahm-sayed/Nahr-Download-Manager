using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.IO;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;

namespace DownloadManager.Engine;

public sealed record ProbeResult(
    Uri FinalUri,
    long? ContentLength,
    bool SupportsRanges,
    string? ETag,
    string? LastModified,
    string? FileName,
    string? ContentType);

public static class HttpProbe
{
    /// <summary>
    /// Sends GET with "Range: bytes=0-0" and reads headers only. Range support is accepted only for a well-formed
    /// 206 whose Content-Range is exactly "bytes 0-0/N"; anything else is treated as "no range support".
    /// If a HEAD/Range GET 0-0 returns 403, falls back to a plain GET with Range: bytes=0-0.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, Uri uri, TimeSpan headersTimeout, CancellationToken ct, DownloadItem? item = null)
    {
        // Try HEAD first with Range: 0-0
        using var probeResult = await ProbeOnceAsync(http, uri, HttpMethod.Head, headersTimeout, ct, item, sendRange: true).ConfigureAwait(false);
        if (probeResult.StatusCode != HttpStatusCode.Forbidden && probeResult.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            return BuildResult(probeResult, uri);
        }

        // Fallback 1: GET with Range: 0-0 (some servers block HEAD but allow ranged GET)
        using var fallback = await ProbeOnceAsync(http, uri, HttpMethod.Get, headersTimeout, ct, item, sendRange: true).ConfigureAwait(false);
        if (fallback.StatusCode != HttpStatusCode.Forbidden)
        {
            return BuildResult(fallback, uri);
        }

        // Fallback 2: Plain GET without Range header (some PDF/journal portals reject Range requests with 403)
        using var plainGet = await ProbeOnceAsync(http, uri, HttpMethod.Get, headersTimeout, ct, item, sendRange: false).ConfigureAwait(false);
        if (plainGet.StatusCode != HttpStatusCode.Forbidden)
        {
            return BuildResult(plainGet, uri);
        }

        // Still 403 - throw with helpful message
        throw new DownloadHttpException(HttpStatusCode.Forbidden, null);
    }

    private static async Task<HttpResponseMessage> ProbeOnceAsync(
        HttpClient http, Uri uri, HttpMethod method, TimeSpan headersTimeout, CancellationToken ct, DownloadItem? item, bool sendRange)
    {
        using var req = new HttpRequestMessage(method, uri);
        ApplyHeaders(req, item, uri);
        if (sendRange)
            req.Headers.Range = new RangeHeaderValue(0, 0);
        req.Headers.AcceptEncoding.ParseAdd("identity");

        return await SendForHeadersAsync(http, req, headersTimeout, ct).ConfigureAwait(false);
    }

    private static void ApplyHeaders(HttpRequestMessage req, DownloadItem? item, Uri uri)
    {
        if (item is null) return;

        // Per-download User-Agent
        if (!string.IsNullOrWhiteSpace(item.UserAgent))
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd(item.UserAgent);
        }

        // Referer: only an explicit value supplied by the user (or later by a browser extension).
        // Never derived or guessed — deriving one to satisfy a site's referer check would be circumvention.
        if (!string.IsNullOrWhiteSpace(item.Referer))
        {
            try
            {
                req.Headers.Referrer = new Uri(item.Referer);
            }
            catch { /* malformed referer, ignore */ }
        }

        // Per-download Accept
        if (!string.IsNullOrWhiteSpace(item.Accept))
        {
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd(item.Accept);
        }

        // Per-download Accept-Language
        if (!string.IsNullOrWhiteSpace(item.AcceptLanguage))
        {
            req.Headers.AcceptLanguage.Clear();
            req.Headers.AcceptLanguage.ParseAdd(item.AcceptLanguage);
        }

        // Extra headers
        if (item.ExtraHeaders is { Count: > 0 })
        {
            foreach (var kvp in item.ExtraHeaders)
            {
                if (req.Headers.Contains(kvp.Key))
                {
                    req.Headers.Remove(kvp.Key);
                }
                req.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }
    }

    private static ProbeResult BuildResult(HttpResponseMessage resp, Uri originalUri)
    {
        var finalUri = resp.RequestMessage?.RequestUri ?? originalUri;
        var etag = resp.Headers.ETag?.ToString();
        var lastModified = FormatLastModified(resp);
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var name = FileNameFrom(resp, finalUri);
        var cr = resp.Content.Headers.ContentRange;

        switch (resp.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                if (cr is { From: 0, To: 0, Length: > 0 })
                    return new ProbeResult(finalUri, cr.Length, true, etag, lastModified, name, contentType);
                // 206 but malformed: do not trust ranges, and the length is unknown.
                return new ProbeResult(finalUri, null, false, etag, lastModified, name, contentType);

            case HttpStatusCode.RequestedRangeNotSatisfiable when cr?.Length == 0:
                return new ProbeResult(finalUri, 0, false, etag, lastModified, name, contentType);

            case >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices:
                return new ProbeResult(finalUri, resp.Content.Headers.ContentLength, false, etag, lastModified, name, contentType);

            default:
                throw new DownloadHttpException(resp.StatusCode, RetryAfterOf(resp));
        }
    }

    internal static async Task<HttpResponseMessage> SendForHeadersAsync(
        HttpClient http, HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the server's response.");
        }
    }

    internal static string? FormatLastModified(HttpResponseMessage resp) =>
        resp.Content.Headers.LastModified?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

    internal static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta;
        return ra.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    internal static string? FileNameFrom(HttpResponseMessage resp, Uri uri)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var uriName = FileNameFromUri(uri);
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(uriName) && !Path.HasExtension(uriName) && !string.IsNullOrWhiteSpace(contentType))
        {
            var inferred = AddInferredExtension(uriName, contentType);
            if (inferred != uriName) return inferred;
        }
        return uriName;
    }

    public static string? FileNameFromUri(Uri uri)
    {
        var seg = uri.Segments.Length > 0 ? uri.Segments[^1] : "";
        seg = seg.TrimEnd('/');
        return string.IsNullOrEmpty(seg) ? null : Uri.UnescapeDataString(seg);
    }

    /// <summary>
    /// Appends an extension to a filename based on Content-Type when the filename has no extension.
    /// </summary>
    public static string AddInferredExtension(string fileName, string? contentType)
    {
        if (Path.GetExtension(fileName).Length > 0) return fileName; // already has extension
        if (string.IsNullOrWhiteSpace(contentType)) return fileName;

        var mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();
        var ext = mediaType switch
        {
            "text/html" => ".html",
            "application/xhtml+xml" => ".html",
            "video/mp4" => ".mp4",
            "video/x-matroska" => ".mkv",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/x-ms-wmv" => ".wmv",
            "video/x-flv" => ".flv",
            "application/zip" => ".zip",
            "application/x-rar-compressed" => ".rar",
            "application/x-7z-compressed" => ".7z",
            "application/gzip" => ".gz",
            "application/x-tar" => ".tar",
            "application/x-bzip2" => ".bz2",
            "application/x-xz" => ".xz",
            "application/pdf" => ".pdf",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.ms-powerpoint" => ".ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/aac" => ".aac",
            "audio/ogg" => ".ogg",
            "audio/mp4" => ".m4a",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/tiff" => ".tiff",
            "image/heic" => ".heic",
            "application/x-msdownload" => ".exe",
            "application/octet-stream" => ".bin",
            _ => null
        };
        return ext is null ? fileName : fileName + ext;
    }
}
