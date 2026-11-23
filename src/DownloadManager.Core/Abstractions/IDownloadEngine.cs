using DownloadManager.Core.Models;

namespace DownloadManager.Core.Abstractions;

public interface IDownloadEngine : IAsyncDisposable
{
    event EventHandler<DownloadProgress>? ProgressChanged;
    event EventHandler<DownloadItem>? StatusChanged;

    Task<DownloadItem> AddAsync(AddDownloadRequest request, CancellationToken ct = default);
    Task<(string FileName, long? ContentLength, string? ContentType)> QuickProbeAsync(
        string url, string? referer = null, string? userAgent = null, CancellationToken ct = default);
    Task<(string FileName, long? ContentLength, string? ContentType)> QuickProbeAsync(
        string url, string? referer, string? userAgent, Dictionary<string, string>? extraHeaders, CancellationToken ct = default) =>
        QuickProbeAsync(url, referer, userAgent, ct);
    Task<IReadOnlyList<(string Label, string Url, string? AudioUrl)>> ProbeHlsQualitiesAsync(
        string url, string? referer = null, string? origin = null, string? userAgent = null, CancellationToken ct = default);
    Task StartAsync(Guid id, CancellationToken ct = default);
    Task PauseAsync(Guid id);
    Task CancelAsync(Guid id, bool deletePartialFile = true);
    Task RemoveAsync(Guid id, bool deleteFile);
    Task<IReadOnlyList<DownloadItem>> RecoverAsync(bool autoResume = false, CancellationToken ct = default);
    Task WaitAsync(Guid id, CancellationToken ct = default);

    DownloadItem? Get(Guid id);
    IReadOnlyList<DownloadItem> GetAll();

    void SetDefaultConnections(int count);
    void SetGlobalSpeedLimit(long bytesPerSecond);
    void SetSpeedLimit(Guid id, long bytesPerSecond);
    void SetMaxConcurrentDownloads(int count);
    void SetQueuePaused(bool paused);
    void SetQueuePriority(Guid id, int priority);
    void SetScheduledStart(Guid id, DateTimeOffset? startAt);
    void SetCategoryConcurrencyLimit(string category, int? limit);
}
