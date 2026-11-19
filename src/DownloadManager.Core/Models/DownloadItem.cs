using System.Net;
using System.Text.Json.Serialization;
using DownloadManager.Core.Security;

namespace DownloadManager.Core.Models;

/// <summary>The complete persistent state of one download. Everything needed for recovery lives here.</summary>
public sealed class DownloadItem
{
    private long _singleStreamBytes;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Category { get; set; } = "General";
    public long? TotalBytes { get; set; }
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? ErrorMessage { get; set; }
    public HttpStatusCode? StatusCode { get; set; }
    public int Connections { get; set; } = 8;
    public long SpeedLimitBytesPerSecond { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<SegmentInfo> Segments { get; set; } = new();

    // Per-download HTTP settings (persisted so pause/resume/restart reuse them)
    public string? UserAgent { get; set; }
    public string? Referer { get; set; }
    public string? Accept { get; set; }
    public string? AcceptLanguage { get; set; }
    public Dictionary<string, string>? ExtraHeaders { get; set; }
    public bool? NeedsHtmlWarning { get; set; }

    /// <summary>Optional group (e.g. a YouTube playlist) this download belongs to. Items sharing a GroupId are shown nested.</summary>
    public string? GroupId { get; set; }

    /// <summary>Display title of the group (e.g. the playlist name).</summary>
    public string? GroupTitle { get; set; }

    /// <summary>Snapshot with sensitive header values removed. Safe to persist or display.</summary>
    public DownloadItem SanitisedSnapshot()
    {
        var copy = (DownloadItem)MemberwiseClone();
        copy.Segments = Segments.Select(s => s.Clone()).ToList();
        copy._singleStreamBytes = SingleStreamBytes;
        if (copy.ExtraHeaders is { Count: > 0 })
            copy.ExtraHeaders = HeaderSanitiser.Sanitize(copy.ExtraHeaders);
        return copy;
    }

    [JsonIgnore] public string TempFilePath => FilePath + ".part";

    /// <summary>Progress of a non-segmented (single connection) download.</summary>
    public long SingleStreamBytes
    {
        get => Volatile.Read(ref _singleStreamBytes);
        set => Volatile.Write(ref _singleStreamBytes, value);
    }

    [JsonIgnore]
    public long DownloadedBytes
    {
        get
        {
            if (Segments.Count == 0) return SingleStreamBytes;
            long sum = 0;
            foreach (var s in Segments) sum += s.Downloaded;
            return sum;
        }
    }

    public long AddSingleStreamBytes(long bytes) => Interlocked.Add(ref _singleStreamBytes, bytes);

    /// <summary>Deep copy, safe to hand to the UI or the store while the engine keeps mutating the original.</summary>
    public DownloadItem Snapshot()
    {
        var copy = (DownloadItem)MemberwiseClone();
        copy.Segments = Segments.Select(s => s.Clone()).ToList();
        copy._singleStreamBytes = SingleStreamBytes;
        return copy;
    }
}
