namespace DownloadManager.Core.Models;

public sealed record DownloadProgress(
    Guid Id,
    DownloadStatus Status,
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    double AverageBytesPerSecond,
    TimeSpan? EstimatedTimeRemaining,
    int ActiveConnections)
{
    public double? Percent => TotalBytes is > 0 ? DownloadedBytes * 100.0 / TotalBytes : null;
}
