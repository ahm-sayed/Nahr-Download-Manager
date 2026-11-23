using System.Collections.Concurrent;
using System.Net;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Core.Security;
using DownloadManager.Engine.Hls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadManager.Engine;

/// <summary>Orchestrates many downloads: add, start, pause, cancel, recover, limits and concurrency.</summary>
public sealed class DownloadEngine : IDownloadEngine
{
    private enum StopReason { None, Pause, Cancel, Shutdown }

    /// <summary>One requested start: queued or running. Shared by the queue wait and the run itself.</summary>
    private sealed class StartTicket
    {
        public required CancellationTokenSource Cts { get; init; }
        public required TaskCompletionSource Completion { get; init; }
        public StopReason Reason;
    }

    private sealed class ActiveRun
    {
        public required StartTicket Ticket { get; init; }
        public required TokenBucketSpeedLimiter Limiter { get; init; }
        public IDisposable? QueueSlot;
        public StopReason Reason { get => Ticket.Reason; set => Ticket.Reason = value; }
    }

    private readonly IDownloadStore _store;
    private readonly EngineOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger _log;
    private sealed record HlsRunConfig(string? Origin, string? CompanionAudioUrl, string? FfmpegPath, bool AudioOnly, string AudioExt, int AudioBitrateKbps);

    private readonly ConcurrentDictionary<Guid, DownloadItem> _items = new();
    private readonly ConcurrentDictionary<Guid, ActiveRun> _runs = new();
    private readonly ConcurrentDictionary<Guid, Func<string, CancellationToken, Task>> _postProcessors = new();
    private readonly ConcurrentDictionary<Guid, HlsRunConfig> _hlsConfigs = new();
    private readonly ConcurrencyGate _gate;
    private readonly TokenBucketSpeedLimiter _globalLimiter;
    private readonly QueueManager _queueManager;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private readonly object _addLock = new();
    private readonly object _startLock = new();
    private readonly ConcurrentDictionary<Guid, StartTicket> _starts = new();
    private int _active;
    private int _peakActive;

    public DownloadEngine(IDownloadStore store, EngineOptions? options = null, HttpClient? http = null, ILogger<DownloadEngine>? logger = null)
    {
        _store = store;
        _options = options ?? new EngineOptions();
        _log = logger ?? NullLogger<DownloadEngine>.Instance;
        _ownsHttp = http is null;
        _http = http ?? EngineHttp.Create(_options);
        _gate = new ConcurrencyGate(_options.MaxConcurrentDownloads);
        _globalLimiter = new TokenBucketSpeedLimiter(_options.GlobalSpeedLimitBytesPerSecond);
        _queueManager = new QueueManager(_options.MaxConcurrentDownloads);
    }

    /// <summary>Queue manager used to start downloads in the order they were added.</summary>
    internal QueueManager Queue => _queueManager;

    /// <summary>Engine options (read-only to callers) for queue integration.</summary>
    internal EngineOptions Options => _options;

    public event EventHandler<DownloadProgress>? ProgressChanged;
    public event EventHandler<DownloadItem>? StatusChanged;

    /// <summary>Number of downloads currently holding a concurrency slot.</summary>
    public int ActiveDownloadCount => Volatile.Read(ref _active);
    /// <summary>Highest <see cref="ActiveDownloadCount"/> seen so far (diagnostics and tests).</summary>
    public int PeakActiveDownloadCount => Volatile.Read(ref _peakActive);

    // ------------------------------------------------------------------ queries

    public DownloadItem? Get(Guid id) => _items.TryGetValue(id, out var i) ? i.Snapshot() : null;

    public IReadOnlyList<DownloadItem> GetAll() => _items.Values.Select(i => i.Snapshot()).OrderBy(i => i.CreatedAt).ToList();

    // ------------------------------------------------------------------ add

    public async Task<IReadOnlyList<(string Label, string Url, string? AudioUrl)>> ProbeHlsQualitiesAsync(
        string url, string? referer = null, string? origin = null, string? userAgent = null, CancellationToken ct = default)
    {
        try
        {
            var variants = await HlsStreamService.ProbeVariantsAsync(_http, url, referer, origin, userAgent, ct).ConfigureAwait(false);
            return variants.Select(v => (v.Label, v.Url, v.AudioUrl)).ToList();
        }
        catch
        {
            return [];
        }
    }

    public Task<(string FileName, long? ContentLength, string? ContentType)> QuickProbeAsync(
        string url, string? referer = null, string? userAgent = null, CancellationToken ct = default) =>
        QuickProbeAsync(url, referer, userAgent, extraHeaders: null, ct);

    public async Task<(string FileName, long? ContentLength, string? ContentType)> QuickProbeAsync(
        string url, string? referer, string? userAgent, Dictionary<string, string>? extraHeaders, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ("download", null, null);

        var fallbackName = PathSafety.SanitizeFileName(HttpProbe.FileNameFromUri(uri));
        try
        {
            var carrier = new DownloadItem
            {
                Url = uri.AbsoluteUri,
                Referer = string.IsNullOrWhiteSpace(referer) ? null : referer,
                UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                ExtraHeaders = extraHeaders is { Count: > 0 } ? new Dictionary<string, string>(extraHeaders) : null,
            };
            var probe = await HttpProbe.ProbeAsync(_http, uri, TimeSpan.FromSeconds(10), ct, carrier).ConfigureAwait(false);
            var name = PathSafety.SanitizeFileName(probe.FileName ?? HttpProbe.FileNameFromUri(uri));
            if (probe.FileName is null) name = HttpProbe.AddInferredExtension(name, probe.ContentType);
            return (name, probe.ContentLength, probe.ContentType);
        }
        catch
        {
            return (fallbackName, null, null);
        }
    }

    public async Task<DownloadItem> AddAsync(AddDownloadRequest request, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDownloadUrlException("Only absolute http:// and https:// URLs are supported.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDownloadUrlException("URLs that embed credentials are not supported.");

        var directory = Path.GetFullPath(request.DestinationDirectory);
        Directory.CreateDirectory(directory);

        // One quick, non-retrying probe so the UI can show name and size immediately, unless already probed.
        ProbeResult? probe = null;
        if (!request.SkipInitialProbe)
        {
            try
            {
                var carrier = new DownloadItem
                {
                    Url = uri.AbsoluteUri,
                    UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent,
                    Referer = string.IsNullOrWhiteSpace(request.Referer) ? null : request.Referer,
                    Accept = string.IsNullOrWhiteSpace(request.Accept) ? null : request.Accept,
                    AcceptLanguage = string.IsNullOrWhiteSpace(request.AcceptLanguage) ? null : request.AcceptLanguage,
                    ExtraHeaders = request.ExtraHeaders,
                };
                probe = await HttpProbe.ProbeAsync(_http, uri, _options.ResponseHeadersTimeout, ct, carrier).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogInformation("Initial probe of {Url} failed ({Reason}); it will be retried when the download starts",
                    UrlRedactor.Redact(uri.AbsoluteUri), ex.Message);
            }
        }

        var name = PathSafety.SanitizeFileName(request.FileName ?? probe?.FileName ?? HttpProbe.FileNameFromUri(uri));
        if (request.FileName is null && probe?.FileName is null && HttpProbe.FileNameFromUri(uri) is not null)
            name = HttpProbe.AddInferredExtension(name, probe?.ContentType);

        DownloadItem item;
        lock (_addLock)
        {
            var path = PathSafety.GetUniqueFilePath(directory, name,
                p => _items.Values.Any(i => string.Equals(i.FilePath, p, StringComparison.OrdinalIgnoreCase)));
            var extraHeaders = request.ExtraHeaders is { Count: > 0 } ? new Dictionary<string, string>(request.ExtraHeaders) : null;
            if (!string.IsNullOrWhiteSpace(request.Origin))
            {
                extraHeaders ??= new Dictionary<string, string>();
                extraHeaders["Origin"] = request.Origin!;
            }

            item = new DownloadItem
            {
                Url = uri.AbsoluteUri,
                FinalUrl = probe?.FinalUri.AbsoluteUri ?? uri.AbsoluteUri,
                FileName = Path.GetFileName(path),
                FilePath = path,
                // An explicit category wins; otherwise classify by the final (sanitised) file name.
                Category = request.Category is { Length: > 0 } and not Categorizer.General ? request.Category : Categorizer.ForFileName(Path.GetFileName(path)),
                TotalBytes = request.KnownTotalBytes ?? probe?.ContentLength,
                ContentType = probe?.ContentType,
                Connections = EngineOptions.ClampConnections(request.Connections ?? _options.DefaultConnections),
                SpeedLimitBytesPerSecond = Math.Max(0, request.SpeedLimitBytesPerSecond),
                GroupId = string.IsNullOrWhiteSpace(request.GroupId) ? null : request.GroupId,
                GroupTitle = string.IsNullOrWhiteSpace(request.GroupTitle) ? null : request.GroupTitle,
                // Per-download HTTP headers. Supplied by the user (or later by the browser extension) only —
                // the engine never fills any of them automatically.
                UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent,
                Referer = string.IsNullOrWhiteSpace(request.Referer) ? null : request.Referer,
                Accept = string.IsNullOrWhiteSpace(request.Accept) ? null : request.Accept,
                AcceptLanguage = string.IsNullOrWhiteSpace(request.AcceptLanguage) ? null : request.AcceptLanguage,
                ExtraHeaders = extraHeaders,
            };
            _items[item.Id] = item;
            if (request.PostProcessAsync is not null) _postProcessors[item.Id] = request.PostProcessAsync;
            if (request.IsHlsStream || HlsStreamService.LooksLikeHlsUrl(uri.AbsoluteUri))
                _hlsConfigs[item.Id] = new HlsRunConfig(
                    request.Origin,
                    request.CompanionAudioUrl,
                    request.FfmpegPath,
                    request.ExtractAudioOnly,
                    request.AudioFormatExt,
                    request.AudioBitrateKbps);
        }

        await _store.SaveAsync(item.SanitisedSnapshot(), ct).ConfigureAwait(false);
        StatusChanged?.Invoke(this, item.SanitisedSnapshot());

        return item.Snapshot();
    }

    // ------------------------------------------------------------------ start / pause / cancel

    /// <summary>
    /// Queues a download. Returns as soon as the item is marked Queued; the queue decides when it actually runs
    /// (FIFO by default, honouring priority, scheduled start and per-category limits). Use <see cref="WaitAsync"/>
    /// to await completion.
    /// </summary>
    public async Task StartAsync(Guid id, CancellationToken ct = default)
    {
        var item = RequireItem(id);
        if (item.Status == DownloadStatus.Completed) return;

        StartTicket ticket;
        lock (_startLock)
        {
            if (_starts.ContainsKey(id)) return;                      // already queued or running

            ticket = new StartTicket
            {
                Cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
                Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _starts[id] = ticket;
        }

        try
        {
            if (item.Status != DownloadStatus.Queued) await TransitionAsync(item, DownloadStatus.Queued).ConfigureAwait(false);
            item.ErrorMessage = null;
        }
        catch
        {
            _starts.TryRemove(id, out _);
            ticket.Cts.Dispose();
            ticket.Completion.TrySetResult();
            throw;
        }

        _ = Task.Run(() => RunWhenQueuedAsync(item, ticket), CancellationToken.None);
    }

    public async Task PauseAsync(Guid id)
    {
        if (_runs.TryGetValue(id, out var run))
        {
            run.Reason = StopReason.Pause;
            CancelQuietly(run.Ticket);
            await run.Ticket.Completion.Task.ConfigureAwait(false);
        }
        else if (_starts.TryGetValue(id, out var queued))
        {
            queued.Reason = StopReason.Pause;                          // still waiting for a queue slot
            CancelQuietly(queued);
            await queued.Completion.Task.ConfigureAwait(false);
        }
        else if (_items.TryGetValue(id, out var item) && DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Paused))
        {
            await TransitionAsync(item, DownloadStatus.Paused).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(Guid id, bool deletePartialFile = true)
    {
        var item = RequireItem(id);
        if (_runs.TryGetValue(id, out var run))
        {
            run.Reason = StopReason.Cancel;
            CancelQuietly(run.Ticket);
            await run.Ticket.Completion.Task.ConfigureAwait(false);
            return;
        }
        if (_starts.TryGetValue(id, out var queued))
        {
            queued.Reason = StopReason.Cancel;
            CancelQuietly(queued);
            await queued.Completion.Task.ConfigureAwait(false);
            return;
        }

        if (!DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Cancelled)) return;
        if (deletePartialFile) DiscardPartialData(item);
        await TransitionAsync(item, DownloadStatus.Cancelled).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id, bool deleteFile)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (_runs.ContainsKey(id) || _starts.ContainsKey(id)) await CancelAsync(id).ConfigureAwait(false);

        TryDelete(item.TempFilePath);
        if (deleteFile && item.Status == DownloadStatus.Completed) TryDelete(item.FilePath);

        _items.TryRemove(id, out _);
        _postProcessors.TryRemove(id, out _);
        _hlsConfigs.TryRemove(id, out _);
        await _store.DeleteAsync(id).ConfigureAwait(false);
    }

    public Task WaitAsync(Guid id, CancellationToken ct = default) =>
        _starts.TryGetValue(id, out var ticket)
            ? ticket.Completion.Task.WaitAsync(ct)
            : Task.CompletedTask;

    private static void CancelQuietly(StartTicket ticket)
    {
        try { ticket.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* the run already finished */ }
    }

    // ------------------------------------------------------------------ recovery

    /// <summary>
    /// Loads persisted downloads. Anything that was in flight when the process died is marked Paused with its last
    /// durable progress; with <paramref name="autoResume"/> they are restarted immediately.
    /// </summary>
    public async Task<IReadOnlyList<DownloadItem>> RecoverAsync(bool autoResume = false, CancellationToken ct = default)
    {
        var recovered = new List<DownloadItem>();
        foreach (var stored in await _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (!_items.TryAdd(stored.Id, stored)) continue;
            if (stored.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading)
            {
                _log.LogInformation("Recovered interrupted download {Name} at {Bytes} bytes", stored.FileName, stored.DownloadedBytes);
                stored.Status = DownloadStatus.Paused;     // recovery bypasses the state machine on purpose
                await _store.SaveAsync(stored.SanitisedSnapshot(), ct).ConfigureAwait(false);
                recovered.Add(stored.SanitisedSnapshot());
            }
        }

        if (autoResume)
            foreach (var r in recovered) await StartAsync(r.Id, ct).ConfigureAwait(false);
        return recovered;
    }

    // ------------------------------------------------------------------ limits

    public void SetDefaultConnections(int count) => _options.DefaultConnections = EngineOptions.ClampConnections(count);

    public void SetGlobalSpeedLimit(long bytesPerSecond) => _globalLimiter.BytesPerSecond = bytesPerSecond;

    public void SetSpeedLimit(Guid id, long bytesPerSecond)
    {
        var item = RequireItem(id);
        item.SpeedLimitBytesPerSecond = Math.Max(0, bytesPerSecond);
        if (_runs.TryGetValue(id, out var run)) run.Limiter.BytesPerSecond = item.SpeedLimitBytesPerSecond;
    }

    public void SetMaxConcurrentDownloads(int count)
    {
        _gate.SetLimit(count);
        _queueManager.SetMaxConcurrent(count);
    }

    public void SetQueuePaused(bool paused) => _queueManager.SetPaused(paused);
    public void SetQueuePriority(Guid id, int priority) => _queueManager.SetPriority(id, priority);
    public void SetScheduledStart(Guid id, DateTimeOffset? startAt) => _queueManager.SetScheduledStart(id, startAt);
    public void SetCategoryConcurrencyLimit(string category, int? limit) => _queueManager.SetCategoryLimit(category, limit);

    // ------------------------------------------------------------------ run loop

    /// <summary>Waits for a queue slot, then a concurrency slot, then runs the job to completion.</summary>
    private async Task RunWhenQueuedAsync(DownloadItem item, StartTicket ticket)
    {
        var run = new ActiveRun { Ticket = ticket, Limiter = new TokenBucketSpeedLimiter(item.SpeedLimitBytesPerSecond) };
        var acquired = false;
        try
        {
            run.QueueSlot = await _queueManager.WaitAsync(item, ticket.Cts.Token).ConfigureAwait(false);
            lock (_startLock) _runs[item.Id] = run;

            await _gate.WaitAsync(ticket.Cts.Token).ConfigureAwait(false);
            acquired = true;
            var now = Interlocked.Increment(ref _active);
            InterlockedMax(ref _peakActive, now);

            if (_hlsConfigs.TryGetValue(item.Id, out var hlsCfg))
            {
                await RunHlsJobAsync(item, hlsCfg, ticket.Cts.Token).ConfigureAwait(false);
            }
            else
            {
                _postProcessors.TryGetValue(item.Id, out var postProcess);
                var job = new DownloadJob(item, _http, _options, _store, new BandwidthThrottle(_globalLimiter, run.Limiter), _log,
                    snapshot => StatusChanged?.Invoke(this, snapshot),
                    progress => ProgressChanged?.Invoke(this, progress),
                    postProcess);
                await job.RunAsync(ticket.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ticket.Cts.IsCancellationRequested)
        {
            await FinishInterruptedAsync(item, ticket.Reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError("Download {Name} failed: {Reason}", item.FileName, ex.Message);
            item.ErrorMessage = ex is DownloadException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            if (ex is DownloadHttpException httpEx) item.StatusCode = httpEx.StatusCode;
            if (DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Failed))
                await TransitionAsync(item, DownloadStatus.Failed).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                Interlocked.Decrement(ref _active);
                _gate.Release();
            }
            run.QueueSlot?.Dispose();
            lock (_startLock)
            {
                _runs.TryRemove(item.Id, out _);
                _starts.TryRemove(item.Id, out _);
            }
            ticket.Cts.Dispose();
            ticket.Completion.TrySetResult();
        }
    }

    private async Task RunHlsJobAsync(DownloadItem item, HlsRunConfig cfg, CancellationToken ct)
    {
        if (DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Probing))
            await TransitionAsync(item, DownloadStatus.Probing).ConfigureAwait(false);
        if (DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Downloading))
            await TransitionAsync(item, DownloadStatus.Downloading).ConfigureAwait(false);

        await HlsStreamService.DownloadHlsToMp4Async(
            _http,
            item.Url,
            item.TempFilePath,
            item.Connections,
            item.Referer,
            cfg.Origin,
            item.UserAgent,
            cfg.CompanionAudioUrl,
            cfg.FfmpegPath,
            (downloaded, estTotal, speed, eta, segments) =>
            {
                item.TotalBytes = estTotal;
                item.Segments = segments.ToList();
                item.SupportsRanges = true;
                ProgressChanged?.Invoke(this, new DownloadProgress(
                    item.Id,
                    DownloadStatus.Downloading,
                    downloaded,
                    estTotal,
                    speed,
                    speed,
                    eta,
                    segments.Count));
            },
            ct,
            cfg.AudioOnly,
            cfg.AudioExt,
            cfg.AudioBitrateKbps).ConfigureAwait(false);

        var target = item.FilePath;
        if (File.Exists(target))
            target = PathSafety.GetUniqueFilePath(Path.GetDirectoryName(target)!, Path.GetFileName(target));
        File.Move(item.TempFilePath, target, overwrite: false);

        var finalBytes = new FileInfo(target).Length;
        item.FilePath = target;
        item.FileName = Path.GetFileName(target);
        item.TotalBytes = finalBytes;
        item.Segments = new List<SegmentInfo>();
        item.SingleStreamBytes = finalBytes;
        item.ErrorMessage = null;
        item.CompletedAt = DateTimeOffset.UtcNow;
        await TransitionAsync(item, DownloadStatus.Completed).ConfigureAwait(false);
    }

    private async Task FinishInterruptedAsync(DownloadItem item, StopReason reason)
    {
        if (reason == StopReason.Cancel)
        {
            DiscardPartialData(item);
            if (DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Cancelled))
                await TransitionAsync(item, DownloadStatus.Cancelled).ConfigureAwait(false);
        }
        else if (DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Paused))
        {
            await TransitionAsync(item, DownloadStatus.Paused).ConfigureAwait(false);
        }
    }

    private async Task TransitionAsync(DownloadItem item, DownloadStatus to)
    {
        DownloadStateMachine.EnsureTransition(item.Status, to);
        item.Status = to;
        try { await _store.SaveAsync(item.SanitisedSnapshot()).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist state of {Name}", item.FileName); }
        StatusChanged?.Invoke(this, item.Snapshot());
    }

    private static void DiscardPartialData(DownloadItem item)
    {
        item.Segments = new List<SegmentInfo>();
        item.SingleStreamBytes = 0;
        TryDelete(item.TempFilePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DownloadItem RequireItem(Guid id) =>
        _items.TryGetValue(id, out var item) ? item : throw new KeyNotFoundException($"No download with id {id}.");

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;                  // dispose is idempotent; App and tests may both call it
        _disposed = true;

        _lifetime.Cancel();                     // reject new starts immediately

        var pending = new List<Task>();
        foreach (var (_, ticket) in _starts)
        {
            ticket.Reason = StopReason.Shutdown;
            CancelQuietly(ticket);
            pending.Add(ticket.Completion.Task);
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        _queueManager.Dispose();
        _lifetime.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
