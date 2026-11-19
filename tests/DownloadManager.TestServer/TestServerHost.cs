using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.TestServer;

/// <summary>
/// In-process Kestrel server. URL shape: /data/{flags}/{size}[/name] where flags is a '+'-separated list:
///   (none)       well-behaved server with full Range support, ETag and Last-Modified
///   norange      no Accept-Ranges, ignores Range, answers 200
///   ignorerange  advertises Accept-Ranges but answers every Range request with 200 + full body
///   lateignore   honours the 0-0 probe, then answers later ranged requests with 200 + full body
///   badrange     honours the probe, then answers 206 with a Content-Range that does not match the request
///   shifted      honours the probe, then serves the range starting ONE BYTE EARLIER with an honest Content-Range
///                (a different range than requested - accepting it would silently corrupt the file)
///   chunked      unknown length (chunked encoding), no ranges
///   flaky        cuts the connection at 40% for the first State.FlakyDropsRemaining large responses
///   stall        freezes at 30% for the first State.StallsRemaining large responses
///   fail         answers 503 (Retry-After: 0) for the first State.FailuresRemaining requests
///   slow         delays State.SlowChunkDelayMs after every 16 KiB chunk
///   named        adds a hostile Content-Disposition filename
///   notfound     404
///   hotlink      serves content only for allowed referer + browser User-Agent, else 403
/// </summary>
public sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestServerHost(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }
    public TestServerState State { get; } = new();

    public Uri Url(string flags, long size, string? name = null, string? customPath = null)
    {
        var path = customPath ?? $"data/{flags}/{size}" + (name is null ? "" : "/" + name);
        return new(BaseUri, path);
    }

    public static async Task<TestServerHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        TestServerHost? host = null;
        app.Run(ctx => host!.HandleAsync(ctx));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        host = new TestServerHost(app, new Uri(address));
        return host;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var segs = ctx.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segs.Length >= 3 && segs[0] == "data" && long.TryParse(segs[2], out var dataSize))
        {
            State.Enter();
            var generation = State.Generation;
            long sent = 0;
            try
            {
                sent = await ServeAsync(ctx, segs[1].Split('+'), dataSize, generation);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // the client went away (timeout, pause, cancel) - normal in these tests
            }
            finally
            {
                if (generation == State.Generation)
                    State.Requests.Enqueue(new RequestRecord(ctx.Request.Path.Value!, ctx.Request.Headers.Range.ToString(),
                        ctx.Response.StatusCode, sent));
                State.Leave();
            }
            return;
        }

        if (segs.Length >= 2 && segs[0] == "hotlink" && long.TryParse(segs[1], out var size))
        {
            State.Enter();
            var generation = State.Generation;
            long sent = 0;
            try
            {
                sent = await ServeHotlinkAsync(ctx, size, generation);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // the client went away (timeout, pause, cancel) - normal in these tests
            }
            finally
            {
                if (generation == State.Generation)
                    State.Requests.Enqueue(new RequestRecord(ctx.Request.Path.Value!, ctx.Request.Headers.Range.ToString(),
                        ctx.Response.StatusCode, sent));
                State.Leave();
            }
            return;
        }

        if (segs.Length >= 2 && segs[0] == "html" && long.TryParse(segs[1], out var htmlSize))
        {
            State.Enter();
            var generation = State.Generation;
            long sent = 0;
            try
            {
                sent = await ServeHtmlAsync(ctx, htmlSize, generation);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // the client went away (timeout, pause, cancel) - normal in these tests
            }
            finally
            {
                if (generation == State.Generation)
                    State.Requests.Enqueue(new RequestRecord(ctx.Request.Path.Value!, ctx.Request.Headers.Range.ToString(),
                        ctx.Response.StatusCode, sent));
                State.Leave();
            }
            return;
        }

        ctx.Response.StatusCode = 404;
    }

    private async Task<long> ServeAsync(HttpContext ctx, string[] flags, long size, int generation)
    {
        bool Has(string f) => Array.IndexOf(flags, f) >= 0;
        var req = ctx.Request;
        var res = ctx.Response;

        if (Has("notfound")) { res.StatusCode = 404; return 0; }
        if (Has("fail") && Interlocked.Decrement(ref State.FailuresRemaining) >= 0)
        {
            res.StatusCode = 503;
            res.Headers["Retry-After"] = "0";
            return 0;
        }

        if (Has("hotlink")) return await ServeHotlinkAsync(ctx, size, generation);

        var seed = State.Version;
        var etag = $"\"v{seed}-{size}\"";
        var lastModified = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(seed).ToString("R", CultureInfo.InvariantCulture);
        res.ContentType = Has("html") ? "text/html; charset=utf-8" : "application/octet-stream";
        res.Headers["ETag"] = etag;
        res.Headers["Last-Modified"] = lastModified;
        if (!Has("norange") && !Has("chunked")) res.Headers["Accept-Ranges"] = "bytes";
        if (Has("named")) res.Headers["Content-Disposition"] = "attachment; filename=\"../..\\evil:na*me?.bin\"";

        var honourRanges = !Has("norange") && !Has("ignorerange") && !Has("chunked");
        long from = 0, to = size - 1;
        var ranged = false;
        if (honourRanges && TryParseRange(req.Headers.Range.ToString(), size, out from, out to))
        {
            var ifRange = req.Headers["If-Range"].ToString();
            ranged = ifRange.Length == 0 || ifRange == etag || ifRange == lastModified;
        }

        var isProbe = from == 0 && to == 0;
        if (ranged && Has("shifted") && from > 0) { from--; to--; }
        if (ranged && Has("lateignore") && !isProbe) ranged = false;
        var wrongHeader = ranged && Has("badrange") && !isProbe;

        long start, count;
        if (ranged)
        {
            res.StatusCode = 206;
            res.Headers["Content-Range"] = wrongHeader ? $"bytes {from + 1}-{to + 1}/{size}" : $"bytes {from}-{to}/{size}";
            start = from;
            count = to - from + 1;
            res.ContentLength = count;
        }
        else
        {
            res.StatusCode = 200;
            start = 0;
            count = size;
            if (!Has("chunked")) res.ContentLength = size;
        }

        long dropAt = long.MaxValue, stallAt = long.MaxValue;
        if (count > 4096 && Has("flaky") && Interlocked.Decrement(ref State.FlakyDropsRemaining) >= 0) dropAt = count * 2 / 5;
        if (count > 4096 && Has("stall") && Interlocked.Decrement(ref State.StallsRemaining) >= 0) stallAt = count * 3 / 10;

        await res.StartAsync(ctx.RequestAborted);
        var buffer = new byte[16 * 1024];
        long sent = 0;
        while (sent < count)
        {
            if (sent >= dropAt)
            {
                await res.Body.FlushAsync(ctx.RequestAborted);
                ctx.Abort();
                return sent;
            }
            if (sent >= stallAt)
            {
                await res.Body.FlushAsync(ctx.RequestAborted);
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);   // until the client times out
            }

            var n = (int)Math.Min(Math.Min(buffer.Length, count - sent), Math.Min(dropAt, stallAt) - sent);
            TestData.Fill(buffer.AsSpan(0, n), start + sent, seed);
            await res.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
            sent += n;
            State.AddBytes(n, generation);
            if (Has("slow")) await Task.Delay(State.SlowChunkDelayMs, ctx.RequestAborted);
        }
        return sent;
    }

    /// <summary>Serves content only for the hotlink "allowed" referer (e.g. the site itself), else 403.</summary>
    private async Task<long> ServeHotlinkAsync(HttpContext ctx, long size, int generation)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.ContentType = "application/octet-stream";
        res.Headers["ETag"] = $"\"v0-{size}\"";
        res.Headers["Last-Modified"] = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(0).ToString("R", CultureInfo.InvariantCulture);
        res.Headers["Accept-Ranges"] = "bytes";

        var referer = req.Headers["Referer"].ToString();
        var ua = req.Headers.UserAgent.ToString();
        bool browserUa = Regex.IsMatch(ua, @"Mozilla|Chrome|Firefox|Safari", RegexOptions.IgnoreCase);
        bool allowed = !string.IsNullOrWhiteSpace(referer) && referer.Contains("hotlink-allowed");

        if (!allowed || !browserUa)
        {
            res.StatusCode = 403;
            return 0;
        }

        if (req.Headers.Range.ToString().StartsWith("bytes="))
        {
            var range = req.Headers.Range.ToString()[6..];
            var parts = range.Split('-');
            var from = long.Parse(parts[0]);
            var to = long.Parse(parts[1]);
            res.StatusCode = 206;
            res.Headers["Content-Range"] = $"bytes {from}-{to}/{size}";
            res.ContentLength = to - from + 1;
            var buffer = new byte[16 * 1024];
            long sent = 0;
            await res.StartAsync(ctx.RequestAborted);
            while (sent < to - from + 1)
            {
                var n = (int)Math.Min(buffer.Length, to - from + 1 - sent);
                TestData.Fill(buffer.AsSpan(0, n), from + sent, 0);
                await res.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                sent += n;
                State.AddBytes(n, generation);
            }
            return sent;
        }

        res.StatusCode = 200;
        res.ContentLength = size;
        var buffer2 = new byte[16 * 1024];
        long sent2 = 0;
        await res.StartAsync(ctx.RequestAborted);
        while (sent2 < size)
        {
            var n2 = (int)Math.Min(buffer2.Length, size - sent2);
            TestData.Fill(buffer2.AsSpan(0, n2), sent2, 0);
            await res.Body.WriteAsync(buffer2.AsMemory(0, n2), ctx.RequestAborted);
            sent2 += n2;
            State.AddBytes(n2, generation);
        }
        return sent2;
    }

    private static bool TryParseRange(string header, long size, out long from, out long to)
    {
        from = to = 0;
        if (!header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',')) return false;
        var parts = header[6..].Split('-');
        if (parts.Length != 2 || parts[0].Length == 0 || !long.TryParse(parts[0], out from)) return false;
        to = parts[1].Length == 0 ? size - 1 : long.TryParse(parts[1], out var t) ? t : -1;
        to = Math.Min(to, size - 1);
        return from <= to && from < size;
    }

    private async Task<long> ServeHtmlAsync(HttpContext ctx, long size, int generation)
    {
        var res = ctx.Response;
        res.ContentType = "text/html; charset=utf-8";
        res.StatusCode = 200;
        res.Headers["ETag"] = $"\"html-{size}\"";
        res.Headers["Last-Modified"] = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("R", CultureInfo.InvariantCulture);

        // Simple HTML content that looks like a webpage
        var html = $"<!DOCTYPE html><html><head><title>Test Page {size}</title></head><body><h1>Test Page</h1><p>This is a test HTML page of size {size} bytes.</p></body></html>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        var paddedSize = Math.Max(size, buffer.Length);
        var toSend = (int)Math.Min(size, buffer.Length);

        var sent = 0;
        await res.StartAsync(ctx.RequestAborted);
        await res.Body.WriteAsync(buffer.AsMemory(0, toSend), ctx.RequestAborted);
        sent = toSend;
        State.AddBytes(sent, generation);
        return sent;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}