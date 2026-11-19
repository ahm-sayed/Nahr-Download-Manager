using DownloadManager.Core.Models;

namespace DownloadManager.Core.Abstractions;

/// <summary>Durable storage for download state. The SQLite implementation lives in DownloadManager.Data.</summary>
public interface IDownloadStore
{
    Task SaveAsync(DownloadItem snapshot, CancellationToken ct = default);
    Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
