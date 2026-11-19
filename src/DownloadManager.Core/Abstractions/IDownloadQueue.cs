using DownloadManager.Core.Models;

namespace DownloadManager.Core.Abstractions;

/// <summary>Schedules download starts in FIFO/priority order and limits global and per-category concurrency.</summary>
public interface IDownloadQueue
{
    /// <summary>Wait until the item can run. Dispose the returned lease when the run ends.</summary>
    Task<IDisposable> WaitAsync(DownloadItem item, CancellationToken ct = default);

    /// <summary>Change the priority of a waiting item. Higher values dispatch first.</summary>
    void SetPriority(Guid itemId, int priority);

    /// <summary>Set an earliest start time for a waiting item; null clears the schedule.</summary>
    void SetScheduledStart(Guid itemId, DateTimeOffset? startAt);

    /// <summary>Set a per-category concurrency limit; null removes the limit.</summary>
    void SetCategoryLimit(string category, int? limit);

    /// <summary>Pause or resume dispatching queued items. Active downloads continue.</summary>
    void SetPaused(bool paused);

    /// <summary>Set the maximum global concurrency.</summary>
    void SetMaxConcurrent(int count);

    int PendingCount { get; }
    int ActiveCount { get; }
}
