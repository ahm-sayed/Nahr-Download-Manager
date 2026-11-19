namespace DownloadManager.Core.Models;

public sealed record AddDownloadRequest(string Url, string DestinationDirectory)
{
    public string? FileName { get; init; }
    public int? Connections { get; init; }
    public long SpeedLimitBytesPerSecond { get; init; }
    /// <summary>"General" (default) means: classify automatically from the file name.</summary>
    public string Category { get; init; } = Categorizer.General;
    /// <summary>Whether to start the download immediately or enqueue it.</summary>
    public bool StartImmediately { get; init; } = true;
    /// <summary>Priority for queue ordering. Higher = runs first. Default 50 (Normal).</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Optional known file size from an earlier probe or media manifest.</summary>
    public long? KnownTotalBytes { get; init; }

    /// <summary>When true, skips the synchronous probe inside AddAsync so the item is added with zero network latency.</summary>
    public bool SkipInitialProbe { get; init; }

    /// <summary>When true, treats Url as an HLS (.m3u8 / #EXTM3U) stream and remuxes segments into .mp4 (or .mp3/.m4a when ExtractAudioOnly is true).</summary>
    public bool IsHlsStream { get; init; }

    /// <summary>When true on an HLS download, extracts audio only into AudioFormatExt at AudioBitrateKbps.</summary>
    public bool ExtractAudioOnly { get; init; }

    public string AudioFormatExt { get; init; } = "mp3";

    public int AudioBitrateKbps { get; init; } = 192;

    /// <summary>Optional separate HLS audio rendition playlist URL.</summary>
    public string? CompanionAudioUrl { get; init; }

    /// <summary>Optional path to ffmpeg executable for HLS/DASH remuxing.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Optional Origin header sent with requests (needed by many embedded streaming CDNs).</summary>
    public string? Origin { get; init; }

    /// <summary>Optional post-download processing step (e.g. muxing DASH audio into video) executed on the .part file before finalization.</summary>
    public Func<string, CancellationToken, Task>? PostProcessAsync { get; init; }

    /// <summary>Optional Referer sent with every request. Derived from the URL origin when null.</summary>
    public string? Referer { get; init; }

    /// <summary>Optional extra HTTP headers (e.g. "Authorization: Bearer ...", "Cookie: ...").</summary>
    public Dictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>Optional User-Agent override for this download.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Optional Accept header override for this download.</summary>
    public string? Accept { get; init; }

    /// <summary>Optional Accept-Language header override for this download.</summary>
    public string? AcceptLanguage { get; init; }

    /// <summary>Optional group (e.g. playlist) identifier; downloads sharing it are shown nested under one entry.</summary>
    public string? GroupId { get; init; }

    /// <summary>Display title of the group (e.g. the playlist name).</summary>
    public string? GroupTitle { get; init; }
}
