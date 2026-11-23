using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.TestServer;

namespace DownloadManager.Verification;

public static class EngineTests
{
    private const long MiB = 1024 * 1024;

    private static async Task<(DownloadItem Item, Guid Id)> StartAsync(TestEnv env, Engine.DownloadEngine engine, Uri url, int connections)
    {
        var item = await engine.AddAsync(new AddDownloadRequest(url.AbsoluteUri, env.Dir) { Connections = connections }, env.Ct);
        await engine.StartAsync(item.Id, env.Ct);
        return (item, item.Id);
    }

    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("range: 5 MiB over 8 connections is byte-exact and truly parallel", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 5;
            const long size = 5 * MiB + 123;
            var engine = env.NewEngine();
            var item = await env.DownloadAsync(engine, env.Server.Url("range+slow", size), 8);
            Assert.FileMatches(item, size);
            Assert.True(item.SupportsRanges, "range support detected");
            Assert.Equal(8, item.Segments.Count, "segments");
            var reqs = env.Server.State.Requests.ToArray();
            Assert.True(reqs.All(r => r.Status == 206), "every request was a ranged 206");
            Assert.True(env.Server.State.MaxConcurrentRequests >= 4, $"max concurrent requests was {env.Server.State.MaxConcurrentRequests}");
        });

        yield return new TestCase("range: tiny file uses a single segment", async env =>
        {
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("range", 10_000), 8);
            Assert.FileMatches(item, 10_000);
            Assert.Equal(1, item.Segments.Count, "segments");
        });

        yield return new TestCase("range: connections are clamped to 1..32", async env =>
        {
            var engine = env.NewEngine();
            foreach (var (requested, expected) in new[] { (100, 32), (1, 1), (0, 1), (8, 8) })
            {
                var item = await env.DownloadAsync(engine, env.Server.Url("range", 4 * MiB), requested);
                Assert.FileMatches(item, 4 * MiB);
                Assert.Equal(expected, item.Segments.Count, $"segments for {requested} connections");
            }
        });

        yield return new TestCase("fallback: server without range support -> single connection", async env =>
        {
            const long size = 2 * MiB + 7;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("norange", size));
            Assert.FileMatches(item, size);
            Assert.True(!item.SupportsRanges && item.Segments.Count == 0, "no segmentation");
            var reqs = env.Server.State.Requests.ToArray();
            Assert.True(reqs.All(r => r.Status == 200), "never a 206");
            Assert.Equal(1, reqs.Count(r => r.Range.Length == 0), "exactly one real (non-probe) download request");
        });

        yield return new TestCase("fallback: Accept-Ranges advertised but ignored", async env =>
        {
            const long size = 2 * MiB + 11;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("ignorerange", size));
            Assert.FileMatches(item, size);
            Assert.True(!item.SupportsRanges, "ranges not trusted");
        });

        yield return new TestCase("fallback: probe OK but later ranged requests answered 200", async env =>
        {
            const long size = 3 * MiB + 5;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("lateignore", size));
            Assert.FileMatches(item, size);
            Assert.True(!item.SupportsRanges, "fell back to single stream");
            Assert.True(env.Server.State.Requests.Any(r => r.Range.Length > 0 && r.Status == 200), "server really answered a range with 200");
        });

        yield return new TestCase("fallback: wrong Content-Range is detected, never corrupts", async env =>
        {
            const long size = 3 * MiB + 5;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("badrange", size));
            Assert.FileMatches(item, size);
            Assert.True(!item.SupportsRanges, "fell back to single stream");
        });

        yield return new TestCase("fallback: a different range than requested (honest header, shifted body) is rejected, never corrupts", async env =>
        {
            const long size = 3 * MiB + 5;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("shifted", size));
            Assert.FileMatches(item, size);
            Assert.True(!item.SupportsRanges, "fell back to single stream");
        });

        yield return new TestCase("unknown content length (chunked) downloads correctly", async env =>
        {
            const long size = 3 * MiB / 2;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("chunked", size));
            Assert.FileMatches(item, size);
            Assert.Equal(size, item.TotalBytes ?? -1, "total learned at the end");
        });

        yield return new TestCase("empty file completes", async env =>
        {
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("range", 0));
            Assert.FileMatches(item, 0);
        });

        yield return new TestCase("reliability: dropped connections are retried and resumed mid-segment", async env =>
        {
            env.Server.State.FlakyDropsRemaining = 6;
            const long size = 4 * MiB;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("range+flaky", size));
            Assert.FileMatches(item, size);
            Assert.True(env.Server.State.Requests.Count >= 16, $"expected retries, saw {env.Server.State.Requests.Count} requests");
        });

        yield return new TestCase("reliability: stalled connections hit the read timeout and recover", async env =>
        {
            env.Server.State.StallsRemaining = 2;
            const long size = 2 * MiB;
            var engine = env.NewEngine(tweak: o => o.ReadTimeout = TimeSpan.FromMilliseconds(400));
            var item = await env.DownloadAsync(engine, env.Server.Url("range+stall", size), 4);
            Assert.FileMatches(item, size);
            Assert.True(env.Server.State.Requests.Count >= 8, $"expected retries, saw {env.Server.State.Requests.Count} requests");
        });

        yield return new TestCase("reliability: temporary 503 responses are retried with backoff", async env =>
        {
            env.Server.State.FailuresRemaining = 4;
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("range+fail", MiB));
            Assert.FileMatches(item, MiB);
            Assert.Equal(4, env.Server.State.Requests.Count(r => r.Status == 503), "503 responses");
        });

        yield return new TestCase("reliability: 404 fails fast, leaves no files", async env =>
        {
            var item = await env.DownloadAsync(env.NewEngine(), env.Server.Url("notfound", MiB));
            Assert.Equal(DownloadStatus.Failed, item.Status, "status");
            Assert.True(item.ErrorMessage?.Contains("404") == true, $"error message: {item.ErrorMessage}");
            Assert.Equal(0, Directory.GetFiles(env.Dir).Length, "files left behind");
            Assert.True(env.Server.State.Requests.Count <= 3, "404 is not retried");
        });

        yield return new TestCase("reliability: retries are bounded, failed download can be retried later", async env =>
        {
            env.Server.State.FailuresRemaining = 1000;
            var engine = env.NewEngine(tweak: o => o.MaxRetries = 2);
            var item = await env.DownloadAsync(engine, env.Server.Url("range+fail", MiB));
            Assert.Equal(DownloadStatus.Failed, item.Status, "status after exhausting retries");
            Assert.True(item.ErrorMessage?.Contains("Giving up") == true, $"error message: {item.ErrorMessage}");
            Assert.Equal(4, env.Server.State.Requests.Count(r => r.Status == 503), "1 add-probe + 1 try + 2 retries");

            env.Server.State.FailuresRemaining = 0;                  // the server recovers
            await engine.StartAsync(item.Id, env.Ct);
            await engine.WaitAsync(item.Id, env.Ct);
            Assert.FileMatches(engine.Get(item.Id)!, MiB);
        });

        yield return new TestCase("reliability: unreachable server -> Failed, not a crash", async env =>
        {
            var engine = env.NewEngine(tweak: o => o.MaxRetries = 2);
            var item = await engine.AddAsync(new AddDownloadRequest("http://127.0.0.1:1/x.bin", env.Dir), env.Ct);
            Assert.Equal("x.bin", item.FileName, "name derived from the URL");
            await engine.StartAsync(item.Id, env.Ct);
            await engine.WaitAsync(item.Id, env.Ct);
            var done = engine.Get(item.Id)!;
            Assert.Equal(DownloadStatus.Failed, done.Status, "status");
            Assert.True(done.ErrorMessage?.Contains("Giving up") == true, $"error message: {done.ErrorMessage}");
        });

        yield return new TestCase("pause/resume: resumes without re-downloading everything", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 6 * MiB;
            var engine = env.NewEngine();
            var (_, id) = await StartAsync(env, engine, env.Server.Url("range+slow", size), 4);
            await env.WaitForFractionAsync(engine, id, 0.30, size);
            await engine.PauseAsync(id);

            var paused = engine.Get(id)!;
            Assert.Equal(DownloadStatus.Paused, paused.Status, "status after pause");
            Assert.True(paused.DownloadedBytes > 0 && paused.DownloadedBytes < size, $"partial progress {paused.DownloadedBytes}");
            Assert.True(File.Exists(paused.TempFilePath), ".part file kept");
            var stored = await env.NewStore().GetAsync(id);
            Assert.True(stored is { Status: DownloadStatus.Paused } && stored.DownloadedBytes == paused.DownloadedBytes, "progress persisted on pause");

            await engine.StartAsync(id, env.Ct);
            await engine.WaitAsync(id, env.Ct);
            await env.Server.State.WaitForIdleAsync(env.Ct);
            Assert.FileMatches(engine.Get(id)!, size);
            Assert.True(env.Server.State.BytesSent < size + size / 4, $"server sent {env.Server.State.BytesSent} bytes for a {size} byte file");
        });

        yield return new TestCase("pause/resume: survives an application restart", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 6 * MiB;
            var a = env.NewEngine();
            var (_, id) = await StartAsync(env, a, env.Server.Url("range+slow", size), 4);
            await env.WaitForFractionAsync(a, id, 0.30, size);
            await a.PauseAsync(id);
            await a.DisposeAsync();

            var b = env.NewEngine(env.NewStore());                 // "new process", same database
            await b.RecoverAsync();
            var loaded = b.Get(id)!;
            Assert.True(loaded.Status == DownloadStatus.Paused && loaded.DownloadedBytes > 0, "state restored from the store");
            await b.StartAsync(id, env.Ct);
            await b.WaitAsync(id, env.Ct);
            Assert.FileMatches(b.Get(id)!, size);
        });

        yield return new TestCase("recovery: process killed mid-download resumes from the last durable snapshot", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 6 * MiB;
            var frozen = new FreezableStore(env.NewStore());
            var a = env.NewEngine(frozen);
            var (_, id) = await StartAsync(env, a, env.Server.Url("range+slow", size), 4);
            await env.WaitForFractionAsync(a, id, 0.40, size);
            frozen.Freeze();                                       // everything after this instant is "lost"
            await a.DisposeAsync();

            var b = env.NewEngine(env.NewStore());
            var recovered = await b.RecoverAsync();
            Assert.Equal(1, recovered.Count, "recovered downloads");
            Assert.Equal(DownloadStatus.Paused, recovered[0].Status, "interrupted download comes back Paused");
            Assert.True(recovered[0].DownloadedBytes > 0 && recovered[0].DownloadedBytes < size, $"stale-but-valid progress {recovered[0].DownloadedBytes}");
            await b.StartAsync(id, env.Ct);
            await b.WaitAsync(id, env.Ct);
            Assert.FileMatches(b.Get(id)!, size);
        });

        yield return new TestCase("resume validation: remote file changed -> safe restart with the new content", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 4 * MiB;
            var engine = env.NewEngine();
            var (_, id) = await StartAsync(env, engine, env.Server.Url("range+slow", size), 4);
            await env.WaitForFractionAsync(engine, id, 0.30, size);
            await engine.PauseAsync(id);

            env.Server.State.Version = 2;                          // new ETag + different bytes
            await engine.StartAsync(id, env.Ct);
            await engine.WaitAsync(id, env.Ct);
            var item = engine.Get(id)!;
            Assert.FileMatches(item, size, seed: 2);
            Assert.True(item.ETag?.Contains("v2") == true, $"ETag refreshed: {item.ETag}");
        });

        yield return new TestCase("cancel: mid-download removes the partial file", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 6 * MiB;
            var engine = env.NewEngine();
            var (_, id) = await StartAsync(env, engine, env.Server.Url("range+slow", size), 4);
            await env.WaitForFractionAsync(engine, id, 0.15, size);
            await engine.CancelAsync(id);
            var item = engine.Get(id)!;
            Assert.Equal(DownloadStatus.Cancelled, item.Status, "status");
            Assert.Equal(0, item.Segments.Count, "progress discarded");
            Assert.Equal(0, Directory.GetFiles(env.Dir).Length, "no files left");
        });

        yield return new TestCase("speed limit: per-download limiter slows the transfer", async env =>
        {
            var engine = env.NewEngine();
            var sw = Stopwatch.StartNew();
            var fast = await env.DownloadAsync(engine, env.Server.Url("range", MiB));
            var unlimited = sw.Elapsed;
            Assert.FileMatches(fast, MiB);

            sw.Restart();
            var slow = await env.DownloadAsync(engine, env.Server.Url("range", MiB), speedLimit: 512 * 1024);
            var limited = sw.Elapsed;
            Assert.FileMatches(slow, MiB);
            Assert.True(limited.TotalSeconds is >= 1.4 and < 6, $"1 MiB at 512 KiB/s took {limited.TotalSeconds:0.00}s");
            Assert.True(limited > unlimited + TimeSpan.FromSeconds(0.8), $"limited {limited.TotalSeconds:0.00}s vs unlimited {unlimited.TotalSeconds:0.00}s");
        });

        yield return new TestCase("speed limit: global limiter is shared by parallel downloads", async env =>
        {
            var engine = env.NewEngine();
            engine.SetGlobalSpeedLimit(512 * 1024);
            var sw = Stopwatch.StartNew();
            var (_, id1) = await StartAsync(env, engine, env.Server.Url("range", MiB), 4);
            var (_, id2) = await StartAsync(env, engine, env.Server.Url("range", MiB), 4);
            await engine.WaitAsync(id1, env.Ct);
            await engine.WaitAsync(id2, env.Ct);
            Assert.FileMatches(engine.Get(id1)!, MiB);
            Assert.FileMatches(engine.Get(id2)!, MiB);
            Assert.True(sw.Elapsed.TotalSeconds is >= 3.0 and < 12, $"2 MiB total at 512 KiB/s global took {sw.Elapsed.TotalSeconds:0.00}s");
        });

        yield return new TestCase("queue: at most N downloads run simultaneously", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 20;
            var engine = env.NewEngine(tweak: o => o.MaxConcurrentDownloads = 2);
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++) ids.Add((await StartAsync(env, engine, env.Server.Url("range+slow", MiB), 1)).Id);
            await Task.Delay(300, env.Ct);
            Assert.Equal(3, engine.GetAll().Count(i => i.Status == DownloadStatus.Queued), "downloads waiting for a slot");
            foreach (var id in ids) await engine.WaitAsync(id, env.Ct);
            foreach (var id in ids) Assert.FileMatches(engine.Get(id)!, MiB);
            Assert.Equal(2, engine.PeakActiveDownloadCount, "peak simultaneous downloads");
        });

        yield return new TestCase("management: hostile Content-Disposition is sanitised, duplicates never overwrite", async env =>
        {
            var engine = env.NewEngine();
            var url = env.Server.Url("range+named", 300_000);
            var a = await env.DownloadAsync(engine, url, 2);
            var b = await env.DownloadAsync(engine, url, 2);
            Assert.Equal("evil_na_me_.bin", a.FileName, "sanitised name");
            Assert.Equal("evil_na_me_ (1).bin", b.FileName, "second download gets a new name");
            Assert.FileMatches(a, 300_000);
            Assert.FileMatches(b, 300_000);
            Assert.Equal(Path.GetFullPath(env.Dir), Path.GetDirectoryName(a.FilePath)!, "stays inside the destination");
            Assert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(env.Dir)!, "evil*").Length, "nothing escaped to the parent directory");
        });

        yield return new TestCase("management: malformed or unsafe URLs are rejected", async env =>
        {
            var engine = env.NewEngine();
            foreach (var bad in new[] { "not a url", "ftp://127.0.0.1/x", "file:///etc/passwd", "http://user:pw@127.0.0.1/x", "" })
                await Assert.ThrowsAsync<InvalidDownloadUrlException>(
                    () => engine.AddAsync(new AddDownloadRequest(bad, env.Dir), env.Ct), $"'{bad}'");
        });

        yield return new TestCase("progress: events are real, monotonic and complete", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 4 * MiB;
            var engine = env.NewEngine();
            var progress = new ConcurrentQueue<DownloadProgress>();
            var statuses = new ConcurrentQueue<DownloadStatus>();
            engine.ProgressChanged += (_, p) => progress.Enqueue(p);
            engine.StatusChanged += (_, i) => statuses.Enqueue(i.Status);

            var item = await env.DownloadAsync(engine, env.Server.Url("range+slow", size), 4);
            Assert.FileMatches(item, size);

            var events = progress.ToArray();
            Assert.True(events.Length >= 5, $"only {events.Length} progress events");
            for (var i = 1; i < events.Length; i++)
                Assert.True(events[i].DownloadedBytes >= events[i - 1].DownloadedBytes, "downloaded bytes never go backwards");
            Assert.True(events.Any(e => e.BytesPerSecond > 0 && e.AverageBytesPerSecond > 0), "speeds reported");
            Assert.True(events.Any(e => e.EstimatedTimeRemaining is not null), "ETA reported");
            Assert.True(events.Max(e => e.ActiveConnections) >= 2, "active connections reported");
            var last = events[^1];
            Assert.True(last.Status == DownloadStatus.Completed && last.DownloadedBytes == size && last.Percent == 100.0, "final event is 100%");

            var seq = statuses.ToArray().Aggregate(new List<DownloadStatus>(), (l, s) => { if (l.Count == 0 || l[^1] != s) l.Add(s); return l; });
            Assert.Equal("Queued,Probing,Downloading,Completed", string.Join(',', seq), "status sequence");
        });

        yield return new TestCase("rate limit: HTTP 429 on excess connections scales concurrency down and completes", async env =>
        {
            const long size = 1 * MiB;
            const int maxConcurrent = 2;
            var active = 0;
            var rejected429 = 0;
            var port = GetFreeTcpPort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(env.Ct);
            var serverLoop = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync().WaitAsync(serverCts.Token); }
                    catch { break; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var req = ctx.Request;
                            var resp = ctx.Response;
                            if (string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                            {
                                resp.StatusCode = 206;
                                resp.Headers["Accept-Ranges"] = "bytes";
                                resp.Headers["Content-Range"] = $"bytes 0-0/{size}";
                                resp.ContentLength64 = 1;
                                resp.Close();
                                return;
                            }

                            var cur = Interlocked.Increment(ref active);
                            try
                            {
                                if (cur > maxConcurrent)
                                {
                                    Interlocked.Increment(ref rejected429);
                                    resp.StatusCode = 429;
                                    resp.Close();
                                    return;
                                }

                                var rangeHeader = req.Headers["Range"];
                                long start = 0, end = size - 1;
                                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                                {
                                    var parts = rangeHeader["bytes=".Length..].Split('-');
                                    start = long.Parse(parts[0]);
                                    if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                                        end = long.Parse(parts[1]);
                                }
                                var len = end - start + 1;
                                resp.StatusCode = 206;
                                resp.Headers["Accept-Ranges"] = "bytes";
                                resp.Headers["Content-Range"] = $"bytes {start}-{end}/{size}";
                                resp.ContentLength64 = len;

                                var buf = new byte[32 * 1024];
                                var offset = start;
                                var remaining = len;
                                while (remaining > 0)
                                {
                                    var n = (int)Math.Min(buf.Length, remaining);
                                    TestData.Fill(buf.AsSpan(0, n), offset, seed: 1);
                                    await resp.OutputStream.WriteAsync(buf.AsMemory(0, n), serverCts.Token);
                                    await Task.Delay(25, serverCts.Token);
                                    offset += n;
                                    remaining -= n;
                                }
                                resp.Close();
                            }
                            finally
                            {
                                Interlocked.Decrement(ref active);
                            }
                        }
                        catch { }
                    });
                }
            });

            try
            {
                var engine = env.NewEngine();
                var item = await env.DownloadAsync(engine, new Uri($"http://127.0.0.1:{port}/mediafire-sim.zip"), connections: 8);
                Assert.FileMatches(item, size);
                Assert.True(Volatile.Read(ref rejected429) >= 2, $"expected server to return HTTP 429 for excess connections, got {rejected429}");
            }
            finally
            {
                serverCts.Cancel();
                listener.Stop();
                await serverLoop;
            }
        });

        yield return new TestCase("rate limit: permanent HTTP 429 on all segments still gives up cleanly", async env =>
        {
            const long size = 512 * 1024;
            var port = GetFreeTcpPort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(env.Ct);
            var serverLoop = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync().WaitAsync(serverCts.Token); }
                    catch { break; }

                    var resp = ctx.Response;
                    if (string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                    {
                        resp.StatusCode = 206;
                        resp.Headers["Accept-Ranges"] = "bytes";
                        resp.Headers["Content-Range"] = $"bytes 0-0/{size}";
                        resp.ContentLength64 = 1;
                    }
                    else
                    {
                        resp.StatusCode = 429;
                    }
                    try { resp.Close(); } catch { }
                }
            });

            try
            {
                var engine = env.NewEngine(tweak: o => o.MaxRetries = 3);
                var item = await env.DownloadAsync(engine, new Uri($"http://127.0.0.1:{port}/always-429.bin"), connections: 4);
                Assert.Equal(DownloadStatus.Failed, item.Status, "permanent 429 should fail cleanly");
                Assert.True(item.ErrorMessage?.Contains("429") == true, $"expected 429 error message, got: {item.ErrorMessage}");
            }
            finally
            {
                serverCts.Cancel();
                listener.Stop();
                await serverLoop;
            }
        });

        yield return new TestCase("auth+probe: cookie-gated server rejecting HEAD and Range with 403 succeeds via plain GET", async env =>
        {
            const long size = 256 * 1024;
            var port = GetFreeTcpPort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(env.Ct);
            var serverLoop = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync().WaitAsync(serverCts.Token); }
                    catch { break; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var req = ctx.Request;
                            var resp = ctx.Response;
                            var cookie = req.Headers["Cookie"] ?? "";
                            if (!cookie.Contains("session=authorized-user", StringComparison.Ordinal) ||
                                string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                                !string.IsNullOrEmpty(req.Headers["Range"]))
                            {
                                resp.StatusCode = 403;
                                resp.Close();
                                return;
                            }

                            resp.StatusCode = 200;
                            resp.ContentType = "application/pdf";
                            resp.ContentLength64 = size;
                            var buf = new byte[32 * 1024];
                            long offset = 0;
                            while (offset < size)
                            {
                                var n = (int)Math.Min(buf.Length, size - offset);
                                TestData.Fill(buf.AsSpan(0, n), offset, seed: 1);
                                await resp.OutputStream.WriteAsync(buf.AsMemory(0, n), serverCts.Token);
                                offset += n;
                            }
                            resp.Close();
                        }
                        catch { }
                    });
                }
            });

            try
            {
                var store = env.NewStore();
                var engine = env.NewEngine(store);
                var item = await engine.AddAsync(new AddDownloadRequest($"http://127.0.0.1:{port}/7077.full.pdf", env.Dir)
                {
                    ExtraHeaders = new Dictionary<string, string> { ["Cookie"] = "session=authorized-user" }
                }, env.Ct);
                await engine.StartAsync(item.Id, env.Ct);
                await engine.WaitAsync(item.Id, env.Ct);

                Assert.FileMatches(engine.Get(item.Id)!, size);
                var stored = await store.GetAsync(item.Id, env.Ct);
                Assert.True(stored?.ExtraHeaders is null || !stored.ExtraHeaders.Values.Any(v => v.Contains("authorized-user")),
                    "Cookie value must never be persisted to the store");
            }
            finally
            {
                serverCts.Cancel();
                listener.Stop();
                await serverLoop;
            }
        });
    }

    private static int GetFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
