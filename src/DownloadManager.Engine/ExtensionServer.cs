using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadManager.Engine;

/// <summary>
/// A tiny HTTP server that listens on localhost for download &amp; media requests from browser extensions.
/// Protocol: POST /add  { "url": "...", "filename": "...", "referrer": "...", "mediaType": "...", "qualities": [...] }
///           GET  /ping → 200 {"status":"ok","app":"Nahr"}
/// </summary>
public sealed class ExtensionServer : IAsyncDisposable
{
    public const int DefaultPort = 36775;

    private readonly HttpListener _listener = new();
    private readonly ILogger _log;
    private readonly ConcurrentQueue<BrowserFallbackItem> _browserFallbacks = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    private sealed record BrowserFallbackItem(string Url, string Host, string? FileName);

    /// <summary>Queues an extension-intercepted URL that failed with HTTP 403 so Chrome can download it directly.</summary>
    public void QueueBrowserFallback(string url, string? fileName = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        while (_browserFallbacks.Count >= 32)
            _browserFallbacks.TryDequeue(out _);

        _browserFallbacks.Enqueue(new BrowserFallbackItem(url, uri.Host.ToLowerInvariant(), fileName));
        _log.LogInformation("Queued automatic browser fallback for host={Host}", uri.Host);
    }

    /// <summary>Raised on a worker thread when a valid download or media request arrives from the browser extension.</summary>
    public event EventHandler<ExtensionDownloadRequest>? DownloadRequested;

    /// <summary>Raised when a second app launch asks the running instance to bring MainWindow to the front.</summary>
    public event EventHandler? ShowMainWindowRequested;

    /// <summary>Raised when a browser extension identifies itself via Origin header or extId query parameter.</summary>
    public event EventHandler<string>? ExtensionOriginObserved;

    /// <summary>Optional desktop name reported by `/show` so a second instance knows whether the running window is on the same desktop.</summary>
    public string? DesktopName { get; set; }

    public int Port { get; }
    public bool IsRunning => _loop is not null && !_loop.IsCompleted;

    public ExtensionServer(int port = DefaultPort, ILogger? logger = null)
    {
        Port = port;
        _log = logger ?? NullLogger.Instance;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExtensionServer));
        if (IsRunning) return;

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = AcceptLoopAsync(_cts.Token);
        _log.LogInformation("Extension server started on port {Port}", Port);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* expected */ }
        }
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            SetCorsHeaders(resp, req);

            var observedOrigin = req.Headers["Origin"];
            if (!string.IsNullOrWhiteSpace(observedOrigin)
                && observedOrigin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                ExtensionOriginObserved?.Invoke(this, observedOrigin);
            }
            else if (req.QueryString["extId"] is { Length: 32 } extId)
            {
                ExtensionOriginObserved?.Invoke(this, extId);
            }

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            var path = req.Url?.AbsolutePath ?? "";

            if (path == "/ping" && req.HttpMethod == "GET")
            {
                await WriteJsonAsync(resp, 200, """{"status":"ok","app":"Nahr"}""").ConfigureAwait(false);
                return;
            }

            if (path == "/show" && req.HttpMethod == "GET")
            {
                ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
                var payload = JsonSerializer.Serialize(new
                {
                    status = "shown",
                    pid = Environment.ProcessId,
                    desktop = DesktopName ?? ""
                });
                await WriteJsonAsync(resp, 200, payload).ConfigureAwait(false);
                return;
            }

            if (path == "/fallbacks" && req.HttpMethod == "GET")
            {
                var items = new List<object>();
                while (_browserFallbacks.TryDequeue(out var fb))
                {
                    items.Add(new { url = fb.Url, host = fb.Host, fileName = fb.FileName });
                }
                var payload = JsonSerializer.Serialize(new { items });
                await WriteJsonAsync(resp, 200, payload).ConfigureAwait(false);
                return;
            }

            if ((path == "/add" || path == "/prompt") && req.HttpMethod == "POST")
            {
                await HandleAddAsync(req, resp).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(resp, 404, """{"error":"not found"}""").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error handling extension request");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* best effort */ }
        }
    }

    private async Task HandleAddAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (req.ContentLength64 > 1_000_000)
        {
            await WriteJsonAsync(resp, 413, """{"error":"request too large"}""").ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new System.IO.StreamReader(req.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        ExtensionDownloadRequest? dlReq;
        try
        {
            dlReq = JsonSerializer.Deserialize<ExtensionDownloadRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            await WriteJsonAsync(resp, 400, """{"error":"invalid JSON"}""").ConfigureAwait(false);
            return;
        }

        if (dlReq is null || string.IsNullOrWhiteSpace(dlReq.Url))
        {
            await WriteJsonAsync(resp, 400, """{"error":"url is required"}""").ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(dlReq.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await WriteJsonAsync(resp, 400, """{"error":"only http/https URLs are supported"}""").ConfigureAwait(false);
            return;
        }

        // Filter valid HTTP(S) quality candidates if supplied
        if (dlReq.Qualities is { Count: > 0 })
        {
            dlReq.Qualities = dlReq.Qualities
                .Where(q => Uri.TryCreate(q.Url, UriKind.Absolute, out var qu) &&
                            (qu.Scheme == Uri.UriSchemeHttp || qu.Scheme == Uri.UriSchemeHttps))
                .ToList();
        }

        _log.LogInformation("Extension sent download request: host={Host} mediaType={MediaType}", uri.Host, dlReq.MediaType ?? "file");
        DownloadRequested?.Invoke(this, dlReq);

        await WriteJsonAsync(resp, 200, """{"status":"accepted"}""").ConfigureAwait(false);
    }

    private static void SetCorsHeaders(HttpListenerResponse resp, HttpListenerRequest req)
    {
        var origin = req.Headers["Origin"] ?? "*";
        resp.AddHeader("Access-Control-Allow-Origin", origin);
        resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");
        resp.AddHeader("Access-Control-Max-Age", "86400");
        resp.AddHeader("Access-Control-Allow-Private-Network", "true");
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, string json)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        resp.Close();
    }
}

/// <summary>One detected media stream/quality candidate sent from a webpage video/audio element.</summary>
public sealed class ExtensionMediaOption
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public string? AudioUrl { get; set; }
    public string? Extension { get; set; }
    public long? FileSize { get; set; }
    public int? Height { get; set; }
    public long? Bandwidth { get; set; }
}

/// <summary>Download or media request received from a browser extension.</summary>
public sealed class ExtensionDownloadRequest
{
    public string Url { get; set; } = "";
    public string? MasterUrl { get; set; }
    public string? SelectedQualityUrl { get; set; }
    public string? PreferredQualityKey { get; set; }
    public string? Filename { get; set; }
    public string? PageTitle { get; set; }
    public string? PageUrl { get; set; }
    public string? Referrer { get; set; }
    public string? Origin { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Browser session Cookie header forwarded in-memory for authenticated downloads; never persisted or logged.</summary>
    public string? Cookies { get; set; }
    public string? MimeType { get; set; }
    public long? FileSize { get; set; }
    public bool PreferAudio { get; set; }
    /// <summary>True when this request came from intercepting a browser download (eligible for automatic HTTP 403 browser fallback).</summary>
    public bool FromBrowserInterception { get; set; }
    /// <summary>"youtube", "playlist", "ytdlp", "hls", "video", "audio", or "file".</summary>
    public string? MediaType { get; set; }
    /// <summary>Optional multiple quality/source URLs detected on a webpage &lt;video&gt; or &lt;audio&gt; player.</summary>
    public List<ExtensionMediaOption>? Qualities { get; set; }
}
