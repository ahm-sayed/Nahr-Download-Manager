using System.Text.Json.Serialization;

namespace DownloadManager.Core.Models;

/// <summary>One contiguous byte range of the remote file. <see cref="End"/> is inclusive.</summary>
public sealed class SegmentInfo
{
    private long _downloaded;

    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }

    /// <summary>Bytes of this segment already written to disk (written by exactly one worker).</summary>
    public long Downloaded
    {
        get => Volatile.Read(ref _downloaded);
        set => Volatile.Write(ref _downloaded, value);
    }

    [JsonIgnore] public long Length => End - Start + 1;
    [JsonIgnore] public bool IsComplete => Downloaded >= Length;
    [JsonIgnore] public long NextOffset => Start + Downloaded;

    public long AddDownloaded(long bytes) => Interlocked.Add(ref _downloaded, bytes);

    /// <summary>
    /// Real bytes received by this connection when <see cref="Downloaded"/> is measured in another unit
    /// (e.g. HLS workers count finished media segments). Not persisted; null for ordinary byte ranges.
    /// </summary>
    [JsonIgnore] public long? ActualBytes { get; set; }

    public SegmentInfo Clone() => new() { Index = Index, Start = Start, End = End, Downloaded = Downloaded, ActualBytes = ActualBytes };
}
