using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Engine;

/// <summary>
/// One run of one download: probe, (re)validate, download segments or a single stream, verify, publish.
/// A pause/cancel surfaces as OperationCanceledException; progress is always flushed and persisted first.
/// </summary>
internal sealed class DownloadJob(
    DownloadItem item,
    HttpClient http,
    EngineOptions options,
    IDownloadStore store,
    BandwidthThrottle throttle,
    ILogger log,
    Action<DownloadItem> onStatus,
    Action<DownloadProgress> onProgress,
    Func<string, CancellationToken, Task>? postProcess = null)
{
    private readonly RetryPolicy _retry = RetryPolicy.From(options);
    private readonly SpeedMeter _meter = new();
    private SafeFileHandle? _handle;
    private Exception? _fault;
    private int _activeConnections;

    // Adaptive concurrency gate for segmented downloads: dynamically scales down concurrent
    // connections when a host returns HTTP 429 (TooManyRequests) or 503 (ServiceUnavailable)
    // instead of burning retries and killing healthy active streams.
    private readonly object _gateLock = new();
    private TaskCompletionSource _gateSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _allowedConcurrency;
    private int _inFlight;
    private int _streaming;
    private long _rateLimitEpoch;
    private int _globalRateLimitFailures;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        var forceSingle = false;
        var restarts = 0;
        while (true)
        {
            try
            {
                await RunOnceAsync(forceSingle, ct).ConfigureAwait(false);
                return;
            }
            catch (RangeNotHonoredException ex) when (!forceSingle)
            {
                log.LogWarning("Server mishandled a range request ({Reason}); falling back to a single connection for {Url}",
                    ex.Message, UrlRedactor.Redact(item.Url));
                forceSingle = true;
                _fault = null;
                ResetProgress();
            }
            catch (RemoteFileChangedException ex) when (restarts < 2)
            {
                log.LogWarning("Remote file changed ({Reason}); restarting {Url} from zero", ex.Message, UrlRedactor.Redact(item.Url));
                restarts++;
                _fault = null;
                ResetProgress();
                item.ETag = null;
                item.LastModified = null;
            }
        }
    }

    // ---------------------------------------------------------------- orchestration

    private async Task RunOnceAsync(bool forceSingle, CancellationToken ct)
    {
        await SetStatusAsync(DownloadStatus.Probing).ConfigureAwait(false);

        var probe = await _retry.ExecuteAsync(
            t => HttpProbe.ProbeAsync(http, new Uri(item.Url), options.ResponseHeadersTimeout, t, item),
            ct, OnRetry("probe")).ConfigureAwait(false);
        item.FinalUrl = probe.FinalUri.AbsoluteUri;

        var segmented = !forceSingle && probe.SupportsRanges && probe.ContentLength is > 0;
        ReconcileWithProbe(probe, segmented);

        // Check if this looks like an HTML page instead of a file
        if (probe.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            item.NeedsHtmlWarning = true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(item.FilePath)!);
        try
        {
            if (segmented) await RunSegmentedAsync(ct).ConfigureAwait(false);
            else await RunSingleAsync(ct).ConfigureAwait(false);
            await FinalizeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CloseHandle();
        }
    }

    /// <summary>Decides whether stored progress is still valid for what the server offers now; otherwise restarts from zero.</summary>
    private void ReconcileWithProbe(ProbeResult probe, bool segmented)
    {
        var hadProgress = item.Segments.Count > 0 || item.SingleStreamBytes > 0;
        if (hadProgress)
        {
            var valid = segmented
                        && item.SupportsRanges
                        && item.TotalBytes == probe.ContentLength
                        && ValidatorsMatch(probe)
                        && SegmentsConsistent(item.TotalBytes!.Value)
                        && File.Exists(item.TempFilePath)
                        && new FileInfo(item.TempFilePath).Length == item.TotalBytes;
            if (valid)
            {
                log.LogInformation("Resuming {Url} at {Done}/{Total} bytes", UrlRedactor.Redact(item.Url), item.DownloadedBytes, item.TotalBytes);
                return;
            }

            log.LogInformation("Stored progress for {Url} is no longer valid; restarting from zero", UrlRedactor.Redact(item.Url));
            ResetProgress();
        }

        item.TotalBytes = probe.ContentLength;
        item.SupportsRanges = segmented;
        item.ETag = probe.ETag;
        item.LastModified = probe.LastModified;
        item.ContentType = probe.ContentType;
    }

    private bool ValidatorsMatch(ProbeResult p) =>
        (item.ETag is null || p.ETag is null || string.Equals(item.ETag, p.ETag, StringComparison.Ordinal)) &&
        (item.LastModified is null || p.LastModified is null || string.Equals(item.LastModified, p.LastModified, StringComparison.Ordinal));

    private bool SegmentsConsistent(long total)
    {
        if (item.Segments.Count == 0) return false;
        long expectedStart = 0;
        foreach (var s in item.Segments.OrderBy(s => s.Index))
        {
            if (s.Start != expectedStart || s.End < s.Start || s.Downloaded < 0 || s.Downloaded > s.Length) return false;
            expectedStart = s.End + 1;
        }
        return expectedStart == total;
    }

    private void ResetProgress()
    {
        item.Segments = new List<SegmentInfo>();
        item.SingleStreamBytes = 0;
        try { File.Delete(item.TempFilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void ApplyDownloadHeaders(HttpRequestMessage req)
    {

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

    // ---------------------------------------------------------------- segmented mode

    private async Task RunSegmentedAsync(CancellationToken ct)
    {
        var total = item.TotalBytes!.Value;
        if (item.Segments.Count == 0)
        {
            item.Connections = EngineOptions.ClampConnections(item.Connections);
            item.Segments = SegmentPlanner.Plan(total, item.Connections, options.MinSegmentSizeBytes);
        }

        OpenTempFile(FileMode.OpenOrCreate);
        if (RandomAccess.GetLength(_handle!) != total) RandomAccess.SetLength(_handle!, total);

        await SetStatusAsync(DownloadStatus.Downloading).ConfigureAwait(false);
        _meter.Start();

        using var monitorCts = new CancellationTokenSource();
        var monitor = MonitorAsync(monitorCts.Token);
        try
        {
            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = item.Segments.Where(s => !s.IsComplete).ToList();
            InitConcurrencyGate(pending.Count);
            log.LogInformation("Downloading {Url}: {Count} segment(s) of {Total} bytes", UrlRedactor.Redact(item.Url), pending.Count, total);

            await Task.WhenAll(pending.Select(s => RunWorkerAsync(s, workerCts))).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (_fault is not null) ExceptionDispatchInfo.Throw(_fault);
        }
        finally
        {
            monitorCts.Cancel();
            await monitor.ConfigureAwait(false);
            await PersistAsync(flush: true).ConfigureAwait(false);
        }
    }

    private void InitConcurrencyGate(int initialConcurrency)
    {
        lock (_gateLock)
        {
            _allowedConcurrency = Math.Max(1, initialConcurrency);
            _inFlight = 0;
            _streaming = 0;
            _rateLimitEpoch = 0;
            _globalRateLimitFailures = 0;
            _cooldownUntil = DateTimeOffset.MinValue;
            Volatile.Write(ref _activeConnections, 0);
        }
    }

    private static bool IsRateLimit(DownloadHttpException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private TaskCompletionSource SwapSignalLocked()
    {
        var old = _gateSignal;
        _gateSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return old;
    }

    private async Task<long> AcquireSlotAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task waitTask;
            var cooldownDelay = TimeSpan.Zero;
            lock (_gateLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (_inFlight < _allowedConcurrency && now >= _cooldownUntil)
                {
                    _inFlight++;
                    Volatile.Write(ref _activeConnections, _inFlight);
                    return _rateLimitEpoch;
                }

                if (now < _cooldownUntil)
                    cooldownDelay = _cooldownUntil - now;
                waitTask = _gateSignal.Task;
            }

            if (cooldownDelay > TimeSpan.Zero)
                await Task.Delay(cooldownDelay, ct).ConfigureAwait(false);
            else
                await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void MarkStreamingStarted()
    {
        lock (_gateLock)
        {
            _streaming++;
            _globalRateLimitFailures = 0;
        }
    }

    private void ReleaseSlot(bool wasStreaming)
    {
        TaskCompletionSource? toSignal;
        lock (_gateLock)
        {
            if (wasStreaming && _streaming > 0) _streaming--;
            if (_inFlight > 0) _inFlight--;
            Volatile.Write(ref _activeConnections, _inFlight);
            toSignal = SwapSignalLocked();
        }
        toSignal.TrySetResult();
    }

    private (bool Fatal, TimeSpan Cooldown, int NewLimit, int ActiveStreaming) ReleaseSlotOnRateLimit(
        bool wasStreaming, DownloadHttpException ex, long acquiredEpoch)
    {
        TaskCompletionSource? toSignal = null;
        var fatal = false;
        TimeSpan delay;
        int newLimit;
        int activeStreaming;

        lock (_gateLock)
        {
            if (wasStreaming && _streaming > 0) _streaming--;
            if (_inFlight > 0) _inFlight--;
            Volatile.Write(ref _activeConnections, _inFlight);

            var now = DateTimeOffset.UtcNow;
            activeStreaming = _streaming;

            if (activeStreaming > 0)
            {
                // Other connections are actively downloading: the server simply caps simultaneous
                // connections (e.g. MediaFire). Clamp concurrency to activeStreaming and park this
                // worker until an active segment finishes — never abort healthy streams.
                if (_allowedConcurrency > activeStreaming)
                    _allowedConcurrency = Math.Max(1, activeStreaming);
                if (acquiredEpoch == _rateLimitEpoch)
                    _rateLimitEpoch++;

                delay = ComputeRateLimitDelay(1, ex);
                var targetCooldown = now + delay;
                if (targetCooldown > _cooldownUntil) _cooldownUntil = targetCooldown;
                newLimit = _allowedConcurrency;
                toSignal = SwapSignalLocked();
            }
            else if (acquiredEpoch < _rateLimitEpoch)
            {
                // A sibling worker from the exact same burst already scaled concurrency down and
                // set the cooldown; simply park until the cooldown / slot opens.
                delay = _cooldownUntil > now ? _cooldownUntil - now : TimeSpan.Zero;
                newLimit = _allowedConcurrency;
                toSignal = SwapSignalLocked();
            }
            else
            {
                _rateLimitEpoch++;
                if (_allowedConcurrency > 1)
                {
                    _allowedConcurrency = Math.Max(1, _allowedConcurrency / 2);
                    delay = ComputeRateLimitDelay(1, ex);
                    var targetCooldown = now + delay;
                    if (targetCooldown > _cooldownUntil) _cooldownUntil = targetCooldown;
                    newLimit = _allowedConcurrency;
                    toSignal = SwapSignalLocked();
                }
                else
                {
                    // Even a single connection got 429/503 with zero active streams: count toward MaxRetries.
                    _globalRateLimitFailures++;
                    if (_globalRateLimitFailures > options.MaxRetries)
                    {
                        fatal = true;
                        delay = TimeSpan.Zero;
                    }
                    else
                    {
                        delay = ComputeRateLimitDelay(_globalRateLimitFailures, ex);
                        var targetCooldown = now + delay;
                        if (targetCooldown > _cooldownUntil) _cooldownUntil = targetCooldown;
                    }
                    newLimit = _allowedConcurrency;
                    toSignal = SwapSignalLocked();
                }
            }
        }

        toSignal?.TrySetResult();
        return (fatal, delay, newLimit, activeStreaming);
    }

    private TimeSpan ComputeRateLimitDelay(int attempt, DownloadHttpException ex)
    {
        var delay = _retry.GetDelay(attempt, ex);
        // For HTTP 429 without an explicit Retry-After header, use a calmer floor (4x BaseRetryDelay,
        // capped at MaxRetryDelay) so strict rate-limiters recover cleanly without getting hammered.
        if (ex.StatusCode == HttpStatusCode.TooManyRequests && ex.RetryAfter is null)
        {
            var floorMs = Math.Min(options.MaxRetryDelay.TotalMilliseconds, options.BaseRetryDelay.TotalMilliseconds * 4);
            if (delay.TotalMilliseconds < floorMs)
                delay = TimeSpan.FromMilliseconds(floorMs);
        }
        return delay;
    }

    private async Task RunWorkerAsync(SegmentInfo seg, CancellationTokenSource workerCts)
    {
        var failures = 0;
        try
        {
            // A successful attempt may still leave the segment incomplete (server clamped the range): keep going.
            while (!seg.IsComplete)
            {
                workerCts.Token.ThrowIfCancellationRequested();
                var epoch = await AcquireSlotAsync(workerCts.Token).ConfigureAwait(false);
                var startedStreaming = false;
                var released = false;
                var before = seg.Downloaded;

                try
                {
                    await DownloadSegmentOnceAsync(seg, () =>
                    {
                        startedStreaming = true;
                        MarkStreamingStarted();
                    }, workerCts.Token).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) when (workerCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (DownloadHttpException ex) when (IsRateLimit(ex))
                {
                    var (fatal, delay, newLimit, activeStreaming) = ReleaseSlotOnRateLimit(startedStreaming, ex, epoch);
                    released = true;

                    if (fatal)
                        throw new DownloadException($"Giving up after {options.MaxRetries} retries: {ex.Message}", ex);

                    log.LogWarning(
                        "Rate limited ({Status}) on segment {Index} of {Url}; scaling concurrency to {Limit} (active={Active}), cooldown {Delay} ms",
                        (int)ex.StatusCode, seg.Index, UrlRedactor.Redact(item.Url), newLimit, activeStreaming, (int)delay.TotalMilliseconds);
                }
                catch (Exception ex) when (RetryPolicy.IsTransient(ex))
                {
                    ReleaseSlot(startedStreaming);
                    released = true;

                    if (seg.Downloaded > before) failures = 0;
                    failures++;
                    if (failures > options.MaxRetries)
                        throw new DownloadException($"Giving up after {options.MaxRetries} retries: {ex.Message}", ex);

                    var delay = _retry.GetDelay(failures, ex);
                    OnRetry($"segment {seg.Index}")(failures, delay, ex);
                    await Task.Delay(delay, workerCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (!released) ReleaseSlot(startedStreaming);
                }
            }
        }
        catch (OperationCanceledException) when (workerCts.IsCancellationRequested)
        {
            // pause, cancel, or a sibling worker failed
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _fault, ex, null);
            workerCts.Cancel();
        }
    }

    private async Task DownloadSegmentOnceAsync(SegmentInfo seg, Action onStreamingStarted, CancellationToken ct)
    {
        if (seg.IsComplete) return;
        var start = seg.NextOffset;
        var end = seg.End;

        using var req = new HttpRequestMessage(HttpMethod.Get, item.FinalUrl);
        ApplyDownloadHeaders(req);
        req.Headers.Range = new RangeHeaderValue(start, end);
        req.Headers.AcceptEncoding.ParseAdd("identity");
        ApplyIfRange(req);

        using var resp = await HttpProbe.SendForHeadersAsync(http, req, options.ResponseHeadersTimeout, ct).ConfigureAwait(false);
        ValidateSegmentResponse(resp, start, end);
        onStreamingStarted();
        var expectedEnd = resp.Content.Headers.ContentRange!.To!.Value;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        try
        {
            var remaining = expectedEnd - start + 1;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(options.ReadTimeout);
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(0, want), timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for data from the server.");
                }

                if (n == 0) throw new IOException("The connection closed before the segment was complete.");

                await throttle.WaitAsync(n, ct).ConfigureAwait(false);
                await WriteAsync(buffer, n, seg.NextOffset, ct).ConfigureAwait(false);
                seg.AddDownloaded(n);
                _meter.Add(n);
                remaining -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ApplyIfRange(HttpRequestMessage req)
    {
        if (item.ETag is not null && EntityTagHeaderValue.TryParse(item.ETag, out var tag) && !tag.IsWeak)
            req.Headers.IfRange = new RangeConditionHeaderValue(tag);
        else if (item.LastModified is not null &&
                 DateTimeOffset.TryParse(item.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm))
            req.Headers.IfRange = new RangeConditionHeaderValue(lm);
    }

    private void CheckValidators(HttpResponseMessage resp)
    {
        var etag = resp.Headers.ETag?.ToString();
        if (item.ETag is not null && etag is not null && !string.Equals(item.ETag, etag, StringComparison.Ordinal))
            throw new RemoteFileChangedException("ETag changed.");
        var lm = HttpProbe.FormatLastModified(resp);
        if (item.LastModified is not null && lm is not null && !string.Equals(item.LastModified, lm, StringComparison.Ordinal))
            throw new RemoteFileChangedException("Last-Modified changed.");
    }

    /// <summary>The heart of "never corrupt a file": every ranged response must prove it is exactly the range we asked for.</summary>
    private void ValidateSegmentResponse(HttpResponseMessage resp, long start, long end)
    {
        CheckValidators(resp);

        switch (resp.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                var cr = resp.Content.Headers.ContentRange;
                if (cr?.From is null || cr.To is null)
                    throw new RangeNotHonoredException("206 response without a usable Content-Range.");
                if (cr.From != start)
                    throw new RangeNotHonoredException($"Requested offset {start} but Content-Range starts at {cr.From}.");
                if (cr.To < start || cr.To > end)
                    throw new RangeNotHonoredException($"Content-Range end {cr.To} is outside the requested range {start}-{end}.");
                if (cr.Length is { } len && item.TotalBytes is { } total && len != total)
                    throw new RemoteFileChangedException("The total length reported by the server changed.");
                var cl = resp.Content.Headers.ContentLength;
                if (cl is not null && cl != cr.To - cr.From + 1)
                    throw new RangeNotHonoredException("Content-Length does not match Content-Range.");
                return;

            case HttpStatusCode.OK:
                throw new RangeNotHonoredException("The server answered a range request with 200 and the full body.");

            case HttpStatusCode.RequestedRangeNotSatisfiable:
                throw new RemoteFileChangedException("The server rejected the range (416); the file changed.");

            default:
                throw new DownloadHttpException(resp.StatusCode, HttpProbe.RetryAfterOf(resp));
        }
    }

    // ---------------------------------------------------------------- single-stream mode

    private async Task RunSingleAsync(CancellationToken ct)
    {
        OpenTempFile(FileMode.Create);
        await SetStatusAsync(DownloadStatus.Downloading).ConfigureAwait(false);
        _meter.Start();
        Volatile.Write(ref _activeConnections, 1);

        using var monitorCts = new CancellationTokenSource();
        var monitor = MonitorAsync(monitorCts.Token);
        try
        {
            log.LogInformation("Downloading {Url} with a single connection", UrlRedactor.Redact(item.Url));
            await _retry.ExecuteAsync(async t =>
            {
                // Without range support a retry has to start from byte zero.
                item.SingleStreamBytes = 0;
                RandomAccess.SetLength(_handle!, 0);
                _meter.Start();
                await SingleAttemptAsync(t).ConfigureAwait(false);
            }, ct, OnRetry("download")).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeConnections, 0);
            monitorCts.Cancel();
            await monitor.ConfigureAwait(false);
            await PersistAsync(flush: true).ConfigureAwait(false);
        }
    }

    private async Task SingleAttemptAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, item.FinalUrl);
        ApplyDownloadHeaders(req);
        req.Headers.AcceptEncoding.ParseAdd("identity");
        using var resp = await HttpProbe.SendForHeadersAsync(http, req, options.ResponseHeadersTimeout, ct).ConfigureAwait(false);

        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DownloadHttpException(resp.StatusCode, HttpProbe.RetryAfterOf(resp));
        CheckValidators(resp);

        var declared = resp.Content.Headers.ContentLength;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        try
        {
            while (true)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(options.ReadTimeout);
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for data from the server.");
                }
                if (n == 0) break;

                await throttle.WaitAsync(n, ct).ConfigureAwait(false);
                await WriteAsync(buffer, n, item.SingleStreamBytes, ct).ConfigureAwait(false);
                item.AddSingleStreamBytes(n);
                _meter.Add(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (declared is { } d && item.SingleStreamBytes != d)
            throw new IOException($"The connection closed after {item.SingleStreamBytes} of {d} bytes.");
    }

    // ---------------------------------------------------------------- completion

    private async Task FinalizeAsync(CancellationToken ct)
    {
        long expected;
        if (item.Segments.Count > 0)
        {
            expected = item.TotalBytes!.Value;
            var incomplete = item.Segments.FirstOrDefault(s => !s.IsComplete);
            if (incomplete is not null)
                throw new DownloadIntegrityException($"Segment {incomplete.Index} is incomplete.");
            if (item.DownloadedBytes != expected || item.Segments.Sum(s => s.Length) != expected)
                throw new DownloadIntegrityException("Segment sizes do not add up to the file size.");
            if (RandomAccess.GetLength(_handle!) != expected)
                throw new DownloadIntegrityException("The temporary file has an unexpected size.");
        }
        else
        {
            expected = item.SingleStreamBytes;
            RandomAccess.SetLength(_handle!, expected);
            item.TotalBytes = expected;
        }

        var handle = _handle!;
        await Task.Run(() => RandomAccess.FlushToDisk(handle)).ConfigureAwait(false);
        CloseHandle();

        if (postProcess is not null)
        {
            await postProcess(item.TempFilePath, ct).ConfigureAwait(false);
            expected = new FileInfo(item.TempFilePath).Length;
            item.TotalBytes = expected;
            item.Segments = new List<SegmentInfo>();
            item.SingleStreamBytes = expected;
        }

        var target = item.FilePath;
        if (File.Exists(target))
            target = PathSafety.GetUniqueFilePath(Path.GetDirectoryName(target)!, Path.GetFileName(target));
        try
        {
            File.Move(item.TempFilePath, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException($"Could not move the finished file into place: {ex.Message}", ex);
        }

        item.FilePath = target;
        item.FileName = Path.GetFileName(target);
        item.ErrorMessage = null;
        item.CompletedAt = DateTimeOffset.UtcNow;
        await SetStatusAsync(DownloadStatus.Completed).ConfigureAwait(false);
        log.LogInformation("Completed {Url}: {Bytes} bytes", UrlRedactor.Redact(item.Url), expected);
    }

    // ---------------------------------------------------------------- plumbing

    private void OpenTempFile(FileMode mode)
    {
        try
        {
            _handle = File.OpenHandle(item.TempFilePath, mode, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException($"Could not create the temporary file: {ex.Message}", ex);
        }
    }

    private void CloseHandle()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private async ValueTask WriteAsync(byte[] buffer, int count, long offset, CancellationToken ct)
    {
        try
        {
            await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, count), offset, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException($"Could not write to disk: {ex.Message}", ex);
        }
    }

    private async Task SetStatusAsync(DownloadStatus status)
    {
        if (item.Status == status) return;
        DownloadStateMachine.EnsureTransition(item.Status, status);
        item.Status = status;
        await PersistAsync(flush: false).ConfigureAwait(false);
        onStatus(item.Snapshot());
        onProgress(BuildProgress());
    }

    /// <summary>
    /// Persistence invariant: capture the snapshot FIRST, flush the file SECOND, store the snapshot THIRD.
    /// Stored progress therefore never claims bytes that are not durably on disk.
    /// </summary>
    private async Task PersistAsync(bool flush)
    {
        try
        {
            var snapshot = item.Snapshot();
            var handle = _handle;
            if (flush && handle is { IsClosed: false })
                await Task.Run(() => RandomAccess.FlushToDisk(handle)).ConfigureAwait(false);
            await store.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not persist state for {Url}", UrlRedactor.Redact(item.Url));
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        // Keep progress ticks and disk-flush persistence on independent loops so FlushToDisk / SQLite writes
        // never stall live speed, ETA, or progress bar movement.
        await Task.WhenAll(RunProgressLoopAsync(token), RunPersistLoopAsync(token)).ConfigureAwait(false);
    }

    private async Task RunProgressLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(options.ProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                onProgress(BuildProgress());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunPersistLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(options.PersistInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await PersistAsync(flush: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private DownloadProgress BuildProgress()
    {
        var done = item.DownloadedBytes;
        var speed = item.Status == DownloadStatus.Downloading ? _meter.CurrentBytesPerSecond : 0;
        var eta = item.TotalBytes is { } total ? SpeedMeter.EstimateRemaining(total - done, speed) : null;
        return new DownloadProgress(item.Id, item.Status, done, item.TotalBytes, speed,
            item.Status == DownloadStatus.Downloading ? _meter.AverageBytesPerSecond : 0, eta,
            Volatile.Read(ref _activeConnections));
    }

    private Action<int, TimeSpan, Exception> OnRetry(string what) => (attempt, delay, ex) =>
        log.LogWarning("Retry {Attempt}/{Max} for {What} of {Url} in {Delay} ms: {Reason}",
            attempt, options.MaxRetries, what, UrlRedactor.Redact(item.Url), (int)delay.TotalMilliseconds, ex.Message);
}