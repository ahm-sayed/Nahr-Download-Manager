namespace DownloadManager.Engine;

public sealed class EngineOptions
{
    public const int MinConnections = 1;
    public const int MaxConnections = 32;

    public int DefaultConnections { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    /// <summary>0 = unlimited.</summary>
    public long GlobalSpeedLimitBytesPerSecond { get; set; }
    public long MinSegmentSizeBytes { get; set; } = 512 * 1024;
    public int BufferSize { get; set; } = 64 * 1024;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ResponseHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxRetries { get; set; } = 6;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public double RetryJitter { get; set; } = 0.2;

    public TimeSpan PersistInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Default User-Agent string sent with every request. Overridable per-download.</summary>
    public string UserAgent { get; set; } = "Nahr/0.1.0";

    /// <summary>Default Accept header.</summary>
    public string Accept { get; set; } = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

    /// <summary>Default Accept-Language header.</summary>
    public string AcceptLanguage { get; set; } = "en-US,en;q=0.9";

    public static int ClampConnections(int value) => Math.Clamp(value, MinConnections, MaxConnections);
}
