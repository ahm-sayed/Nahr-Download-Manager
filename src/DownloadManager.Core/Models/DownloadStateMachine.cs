namespace DownloadManager.Core.Models;

public static class DownloadStateMachine
{
    private static readonly Dictionary<DownloadStatus, DownloadStatus[]> Allowed = new()
    {
        [DownloadStatus.Queued] = [DownloadStatus.Probing, DownloadStatus.Paused, DownloadStatus.Cancelled, DownloadStatus.Failed],
        [DownloadStatus.Probing] = [DownloadStatus.Downloading, DownloadStatus.Completed, DownloadStatus.Paused, DownloadStatus.Failed, DownloadStatus.Cancelled],
        [DownloadStatus.Downloading] = [DownloadStatus.Probing, DownloadStatus.Completed, DownloadStatus.Paused, DownloadStatus.Failed, DownloadStatus.Cancelled],
        [DownloadStatus.Paused] = [DownloadStatus.Queued, DownloadStatus.Cancelled],
        [DownloadStatus.Failed] = [DownloadStatus.Queued, DownloadStatus.Cancelled],
        [DownloadStatus.Cancelled] = [DownloadStatus.Queued],
        [DownloadStatus.Completed] = [],
    };

    public static bool CanTransition(DownloadStatus from, DownloadStatus to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static void EnsureTransition(DownloadStatus from, DownloadStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Illegal download state transition {from} -> {to}.");
    }

    public static bool IsTerminal(DownloadStatus s) => s is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;
}
