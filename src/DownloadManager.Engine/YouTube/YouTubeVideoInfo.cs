namespace DownloadManager.Engine.YouTube;

/// <summary>One available format (video or audio quality level) for a media URL.</summary>
public sealed record YouTubeFormat(
    string FormatId,
    string Label,
    string? Extension,
    long? FileSizeApprox,
    int? Height,
    int? Width,
    string? VideoCodec,
    string? AudioCodec,
    bool HasVideo,
    bool HasAudio,
    bool IsCombined,
    string? DirectUrl = null,
    string? AudioDirectUrl = null,
    long? AudioSizeApprox = null,
    string? UserAgent = null,
    int? AudioBitrateKbps = null)
{
    public bool NeedsAudioMerge => HasVideo && !HasAudio && !string.IsNullOrWhiteSpace(AudioDirectUrl);

    public long? TotalEstimatedBytes =>
        FileSizeApprox is > 0
            ? FileSizeApprox.Value + (NeedsAudioMerge && AudioSizeApprox is > 0 ? AudioSizeApprox.Value : 0)
            : null;

    public string EffectiveExtension =>
        !HasVideo && HasAudio ? (Extension ?? "m4a") : (NeedsAudioMerge ? "mp4" : (Extension ?? "mp4"));

    /// <summary>Clean human-readable quality label, e.g. "1080p Full HD (MP4 · 45.2 MB)" or "M4A · 128 kbps (3.1 MB)".</summary>
    public string DisplayLabel
    {
        get
        {
            string head;
            if (HasVideo && Height is > 0)
            {
                var badge = Height.Value switch
                {
                    >= 2160 => " 4K",
                    >= 1440 => " 2K",
                    1080 => " Full HD",
                    720 => " HD",
                    _ => "",
                };
                head = $"{Height.Value}p{badge}";
            }
            else if (!HasVideo && HasAudio)
            {
                var extUp = (Extension ?? "m4a").ToUpperInvariant();
                head = AudioBitrateKbps is > 0 ? $"{extUp} — {AudioBitrateKbps.Value} kbps" : $"{extUp} Audio";
            }
            else
            {
                head = Label;
            }

            var details = new List<string>();
            if (HasVideo) details.Add(EffectiveExtension.ToUpperInvariant());
            if (TotalEstimatedBytes is > 0)
            {
                var mb = TotalEstimatedBytes.Value / (1024.0 * 1024.0);
                details.Add(mb >= 1024 ? $"{mb / 1024:F1} GB" : mb >= 10 ? $"{mb:F0} MB" : $"{mb:F1} MB");
            }
            return details.Count > 0 ? $"{head} ({string.Join(" · ", details)})" : head;
        }
    }
}

/// <summary>Metadata for a single YouTube (or yt-dlp supported) video/audio item.</summary>
public sealed class YouTubeVideoInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Uploader { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public required IReadOnlyList<YouTubeFormat> Formats { get; init; }
    /// <summary>Clean, deduplicated VIDEO quality choices (sorted descending by resolution).</summary>
    public IReadOnlyList<YouTubeFormat> QualityOptions { get; init; } = [];
    /// <summary>Clean, deduplicated native AUDIO-ONLY stream choices (sorted descending by bitrate).</summary>
    public IReadOnlyList<YouTubeFormat> AudioOptions { get; init; } = [];
    public string? DirectUrl { get; init; }
    public string? OriginalUrl { get; init; }
}

/// <summary>Metadata for a YouTube playlist.</summary>
public sealed class YouTubePlaylistInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Uploader { get; init; }
    public required IReadOnlyList<YouTubePlaylistEntry> Entries { get; init; }
}

/// <summary>One video entry inside a playlist (flat listing — no formats extracted yet).</summary>
public sealed record YouTubePlaylistEntry(string VideoId, string Title, string Url, TimeSpan? Duration);
