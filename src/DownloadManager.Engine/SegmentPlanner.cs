using DownloadManager.Core.Models;

namespace DownloadManager.Engine;

public static class SegmentPlanner
{
    /// <summary>Splits [0, total) into contiguous, non-overlapping ranges. Never more segments than the file needs.</summary>
    public static List<SegmentInfo> Plan(long totalBytes, int connections, long minSegmentSize)
    {
        var list = new List<SegmentInfo>();
        if (totalBytes <= 0) return list;

        var minSize = Math.Max(1, minSegmentSize);
        var byMinSize = (totalBytes + minSize - 1) / minSize;
        var count = (int)Math.Max(1, Math.Min(EngineOptions.ClampConnections(connections), byMinSize));

        var baseLength = totalBytes / count;
        var remainder = totalBytes % count;
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var length = baseLength + (i < remainder ? 1 : 0);
            list.Add(new SegmentInfo { Index = i, Start = start, End = start + length - 1 });
            start += length;
        }
        return list;
    }
}
