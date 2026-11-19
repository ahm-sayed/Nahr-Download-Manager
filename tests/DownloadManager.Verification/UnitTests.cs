using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Core.Security;
using DownloadManager.Engine;

namespace DownloadManager.Verification;

public static class UnitTests
{
    private static void Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new AssertionException($"{what}: expected {typeof(T).Name}");
    }

    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("unit: PathSafety neutralises hostile file names", _ =>
        {
            var cases = new (string? Input, string Expected)[]
            {
                ("../../Windows/system32/evil.dll", "evil.dll"),
                ("..\\..\\evil.exe", "evil.exe"),
                ("C:\\Users\\x\\a.txt", "a.txt"),
                ("/etc/passwd", "passwd"),
                ("con.txt", "_con.txt"),
                ("NUL", "_NUL"),
                ("a<b>c:d?.txt", "a_b_c_d_.txt"),
                ("report. ", "report"),
                ("..", "download"),
                ("", "download"),
                ("   ", "download"),
                ("dir/", "download"),
                (null, "download"),
            };
            foreach (var (input, expected) in cases)
                Assert.Equal(expected, PathSafety.SanitizeFileName(input), $"Sanitize({input ?? "null"})");

            var longName = PathSafety.SanitizeFileName(new string('a', 300) + ".zip");
            Assert.True(longName.Length <= PathSafety.MaxFileNameLength && longName.EndsWith(".zip"), "long names are truncated but keep their extension");
            return Task.CompletedTask;
        });

        yield return new TestCase("unit: PathSafety never overwrites existing files", env =>
        {
            var dir = env.Dir;
            File.WriteAllText(Path.Combine(dir, "a.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "a (1).bin"), "x");
            Assert.Equal(Path.Combine(dir, "a (2).bin"), PathSafety.GetUniqueFilePath(dir, "a.bin"), "skips existing names");

            File.WriteAllText(Path.Combine(dir, "b.bin.part"), "x");
            Assert.Equal(Path.Combine(dir, "b (1).bin"), PathSafety.GetUniqueFilePath(dir, "b.bin"), "a .part file reserves the name");

            var reserved = Path.Combine(dir, "c.bin");
            Assert.Equal(Path.Combine(dir, "c (1).bin"), PathSafety.GetUniqueFilePath(dir, "c.bin", p => p == reserved), "in-memory reservation");
            return Task.CompletedTask;
        });

        yield return new TestCase("unit: PathSafety rejects path traversal", env =>
        {
            PathSafety.EnsureInsideDirectory(env.Dir, Path.Combine(env.Dir, "ok.bin"));
            Throws<UnauthorizedAccessException>(() => PathSafety.EnsureInsideDirectory(env.Dir, Path.Combine(env.Dir, "..", "x.txt")), "parent directory");
            Throws<UnauthorizedAccessException>(() => PathSafety.EnsureInsideDirectory(env.Dir, env.Dir + "-evil" + Path.DirectorySeparatorChar + "x.txt"), "sibling with common prefix");
            return Task.CompletedTask;
        });

        yield return new TestCase("unit: state machine allows only legal transitions", _ =>
        {
            Assert.True(DownloadStateMachine.CanTransition(DownloadStatus.Queued, DownloadStatus.Probing), "Queued->Probing");
            Assert.True(DownloadStateMachine.CanTransition(DownloadStatus.Downloading, DownloadStatus.Paused), "Downloading->Paused");
            Assert.True(DownloadStateMachine.CanTransition(DownloadStatus.Paused, DownloadStatus.Queued), "Paused->Queued");
            Assert.True(DownloadStateMachine.CanTransition(DownloadStatus.Failed, DownloadStatus.Queued), "Failed->Queued (retry)");
            Assert.True(!DownloadStateMachine.CanTransition(DownloadStatus.Completed, DownloadStatus.Queued), "Completed is final");
            Assert.True(!DownloadStateMachine.CanTransition(DownloadStatus.Paused, DownloadStatus.Downloading), "Paused->Downloading must go through Queued");
            Assert.True(!DownloadStateMachine.CanTransition(DownloadStatus.Queued, DownloadStatus.Queued), "no self transitions");
            Throws<InvalidOperationException>(() => DownloadStateMachine.EnsureTransition(DownloadStatus.Completed, DownloadStatus.Downloading), "EnsureTransition");
            return Task.CompletedTask;
        });

        yield return new TestCase("unit: SegmentPlanner covers the file exactly, without gaps or overlaps", _ =>
        {
            foreach (var size in new long[] { 1, 7, 1000, 1_048_576, 5_000_003 })
            foreach (var conns in new[] { -3, 0, 1, 8, 32, 100 })
            foreach (var min in new long[] { 1, 1000, 512 * 1024 })
            {
                var segs = SegmentPlanner.Plan(size, conns, min);
                var label = $"size={size} conns={conns} min={min}";
                var maxByMin = Math.Max(1, (size + min - 1) / min);
                Assert.True(segs.Count >= 1 && segs.Count <= EngineOptions.ClampConnections(conns), $"{label}: segment count {segs.Count} out of bounds");
                Assert.True(segs.Count <= maxByMin, $"{label}: more segments than the minimum size allows");
                long next = 0;
                foreach (var s in segs)
                {
                    Assert.Equal(next, s.Start, $"{label}: contiguous start");
                    Assert.True(s.End >= s.Start, $"{label}: non-empty segment");
                    next = s.End + 1;
                }
                Assert.Equal(size, next, $"{label}: total coverage");
            }
            Assert.Equal(0, SegmentPlanner.Plan(0, 8, 1000).Count, "empty file has no segments");
            return Task.CompletedTask;
        });

        yield return new TestCase("unit: RetryPolicy backoff, Retry-After and classification", async env =>
        {
            var p = new RetryPolicy(5, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), jitter: 0);
            double[] expected = [100, 200, 400, 800, 1000, 1000];
            for (var i = 1; i <= expected.Length; i++)
                Assert.Equal(expected[i - 1], p.GetDelay(i).TotalMilliseconds, $"delay #{i}");

            var withRetryAfter = p.GetDelay(1, new DownloadHttpException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMilliseconds(700)));
            Assert.Equal(700.0, withRetryAfter.TotalMilliseconds, "Retry-After raises the delay");
            var capped = p.GetDelay(1, new DownloadHttpException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5)));
            Assert.Equal(1000.0, capped.TotalMilliseconds, "Retry-After is capped");

            Assert.True(RetryPolicy.IsTransient(new DownloadHttpException(HttpStatusCode.ServiceUnavailable)), "503 transient");
            Assert.True(RetryPolicy.IsTransient(new DownloadHttpException(HttpStatusCode.TooManyRequests)), "429 transient");
            Assert.True(!RetryPolicy.IsTransient(new DownloadHttpException(HttpStatusCode.NotFound)), "404 permanent");
            Assert.True(!RetryPolicy.IsTransient(new DownloadHttpException(HttpStatusCode.Forbidden)), "403 permanent");
            Assert.True(RetryPolicy.IsTransient(new IOException("reset")), "IO transient");
            Assert.True(RetryPolicy.IsTransient(new TimeoutException()), "timeout transient");
            Assert.True(RetryPolicy.IsTransient(new HttpRequestException("refused")), "network transient");
            Assert.True(!RetryPolicy.IsTransient(new HttpRequestException("tls", new AuthenticationException("bad cert"))), "TLS validation failures are never retried");
            Assert.True(!RetryPolicy.IsTransient(new DownloadFileException("disk full")), "disk errors permanent");
            Assert.True(!RetryPolicy.IsTransient(new RangeNotHonoredException("x")), "range errors are handled by fallback, not retry");

            var fast = new RetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), 0);
            var calls = 0;
            var retries = 0;
            await fast.ExecuteAsync(_ => ++calls < 3 ? throw new IOException("flaky") : Task.CompletedTask, env.Ct, (_, _, _) => retries++);
            Assert.True(calls == 3 && retries == 2, $"succeeds on 3rd attempt (calls={calls}, retries={retries})");

            calls = 0;
            await Assert.ThrowsAsync<DownloadHttpException>(
                () => fast.ExecuteAsync(_ => { calls++; throw new DownloadHttpException(HttpStatusCode.NotFound); }, env.Ct), "404 propagates");
            Assert.Equal(1, calls, "permanent errors are not retried");

            calls = 0;
            try { await fast.ExecuteAsync(_ => { calls++; throw new IOException("down"); }, env.Ct); throw new AssertionException("should have given up"); }
            catch (DownloadException ex) { Assert.True(ex.Message.Contains("Giving up") && calls == 4, $"gave up after 1 + 3 retries (calls={calls})"); }

            // generic overload returns the value and retries (regression: it used to recurse into itself)
            var genericCalls = 0;
            var value = await fast.ExecuteAsync(_ => ++genericCalls < 2 ? throw new IOException("once") : Task.FromResult(42), env.Ct);
            Assert.True(value == 42 && genericCalls == 2, $"generic ExecuteAsync returned {value} after {genericCalls} calls");

            // progress resets the failure counter
            var attempts = 0;
            long progress = 0;
            await fast.ExecuteAsync(_ =>
            {
                attempts++;
                progress++;                                  // every failed attempt still moved forward
                return attempts < 10 ? throw new IOException("slow but steady") : Task.CompletedTask;
            }, env.Ct, progress: () => progress);
            Assert.Equal(10, attempts, "a flaky-but-progressing connection is never abandoned");
        });

        yield return new TestCase("unit: TokenBucketSpeedLimiter enforces the configured rate", async env =>
        {
            var limiter = new TokenBucketSpeedLimiter(100 * 1024);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 15; i++)                     // 150 KiB at 100 KiB/s
            {
                var delay = limiter.Reserve(10 * 1024);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, env.Ct);
            }
            Assert.True(sw.Elapsed.TotalSeconds is >= 1.2 and <= 2.6, $"150 KiB at 100 KiB/s took {sw.Elapsed.TotalSeconds:0.00}s");

            limiter.BytesPerSecond = 0;
            Assert.Equal(TimeSpan.Zero, limiter.Reserve(10_000_000), "limit 0 means unlimited");
        });

        yield return new TestCase("unit: SpeedMeter measures real bytes and estimates ETA", async env =>
        {
            var meter = new SpeedMeter(TimeSpan.FromSeconds(1));
            meter.Start();
            for (var i = 0; i < 10; i++)
            {
                meter.Add(100_000);
                await Task.Delay(50, env.Ct);
            }
            var avg = meter.AverageBytesPerSecond;
            Assert.True(avg is > 500_000 and < 4_000_000, $"average speed {avg:0} B/s for 1 MB in ~0.5 s");
            Assert.True(meter.CurrentBytesPerSecond > 0, "current speed is positive while data flows");
            Assert.Equal(TimeSpan.FromSeconds(10), SpeedMeter.EstimateRemaining(1000, 100)!.Value, "ETA = remaining / speed");
            Assert.True(SpeedMeter.EstimateRemaining(1000, 0) is null, "no ETA at zero speed");
            await Task.Delay(1200, env.Ct);
            Assert.True(meter.CurrentBytesPerSecond < 1, "current speed decays to zero when data stops");
        });

        yield return new TestCase("unit: ConcurrencyGate limits, resizes and handles cancellation", async env =>
        {
            var gate = new ConcurrencyGate(2);
            await gate.WaitAsync(env.Ct);
            await gate.WaitAsync(env.Ct);

            var third = gate.WaitAsync(env.Ct);
            await Task.Delay(50, env.Ct);
            Assert.True(!third.IsCompleted, "third waiter must wait while the limit is 2");
            gate.Release();
            await third.WaitAsync(TimeSpan.FromSeconds(2), env.Ct);

            var fourth = gate.WaitAsync(env.Ct);
            await Task.Delay(50, env.Ct);
            Assert.True(!fourth.IsCompleted, "limit reached again");
            gate.SetLimit(3);
            await fourth.WaitAsync(TimeSpan.FromSeconds(2), env.Ct);

            using var cts = new CancellationTokenSource();
            var fifth = gate.WaitAsync(cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => fifth, "cancelled waiter");
            gate.Release();
            Assert.True(gate.WaitAsync(env.Ct).IsCompletedSuccessfully, "a cancelled waiter must not leak a slot");
        });

        yield return new TestCase("unit: connection clamp and URL redaction", _ =>
        {
            Assert.Equal(1, EngineOptions.ClampConnections(0), "0 -> 1");
            Assert.Equal(1, EngineOptions.ClampConnections(-5), "-5 -> 1");
            Assert.Equal(8, EngineOptions.ClampConnections(8), "8 stays");
            Assert.Equal(32, EngineOptions.ClampConnections(100), "100 -> 32");

            var redacted = UrlRedactor.Redact("https://user:pw@example.com/private/b/file.zip?token=SECRET#frag");
            Assert.True(!redacted.Contains("SECRET") && !redacted.Contains("pw") && !redacted.Contains("private") && !redacted.Contains("user"),
                $"redaction leaked something: {redacted}");
            Assert.True(redacted.Contains("example.com") && redacted.Contains("file.zip"), $"redaction lost host/file: {redacted}");
            Assert.Equal("<invalid-url>", UrlRedactor.Redact("not a url"), "invalid urls");
            return Task.CompletedTask;
        });
    }
}
