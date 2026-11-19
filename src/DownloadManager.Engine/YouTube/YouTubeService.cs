using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadManager.Engine.YouTube;

/// <summary>
/// Wraps yt-dlp (and optional ffmpeg for high-res DASH audio muxing) to extract YouTube video metadata and direct stream URLs
/// in a single fast invocation so <see cref="DownloadEngine"/> can download them with multi-segment acceleration.
/// </summary>
public sealed class YouTubeService
{
    private static readonly Regex YoutubeUrlRegex = new(
        @"(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?.*v=|shorts/|embed/|v/|playlist\?.*list=)|youtu\.be/)[\w\-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaylistUrlRegex = new(
        @"(?:https?://)?(?:www\.|m\.)?youtube\.com/(?:playlist\?|watch\?.+&)list=[\w\-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient AudioHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string? _ytDlpPath;
    private readonly string? _ffmpegPath;
    private readonly ILogger _log;

    public YouTubeService(ILogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
        _ytDlpPath = FindExecutable("yt-dlp.exe");
        _ffmpegPath = FindExecutable("ffmpeg.exe");
    }

    /// <summary>Whether yt-dlp was found on this machine.</summary>
    public bool IsAvailable => _ytDlpPath is not null;

    /// <summary>Whether ffmpeg was found (enables lossless video+audio muxing for 720p/1080p/4K DASH streams).</summary>
    public bool HasFfmpeg => _ffmpegPath is not null;

    /// <summary>Resolved path to yt-dlp (null if not found).</summary>
    public string? YtDlpPath => _ytDlpPath;

    /// <summary>Resolved path to ffmpeg (null if not found).</summary>
    public string? FfmpegPath => _ffmpegPath;

    /// <summary>Returns true when the URL looks like a YouTube video or playlist URL.</summary>
    public static bool IsYouTubeUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && YoutubeUrlRegex.IsMatch(url);

    /// <summary>Returns true when the URL contains a playlist identifier.</summary>
    public static bool IsPlaylistUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && PlaylistUrlRegex.IsMatch(url);

    /// <summary>Extracts the playlist ID from a URL, or null.</summary>
    public static string? ExtractPlaylistId(string url)
    {
        var match = Regex.Match(url, @"[?&]list=([\w\-]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extracts the video ID from a URL, or null.</summary>
    public static string? ExtractVideoId(string url)
    {
        var match = Regex.Match(url, @"(?:v=|youtu\.be/|shorts/|embed/|v/)([\w\-]{11})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------ video info

    /// <summary>Gets metadata, clean quality options, and direct CDN URLs in ONE yt-dlp call.</summary>
    public async Task<YouTubeVideoInfo> GetVideoInfoAsync(string url, CancellationToken ct = default)
    {
        EnsureAvailable();

        var json = await RunProcessAsync(_ytDlpPath!, ["-J", "--no-playlist", "--no-warnings", "--no-download", url], ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return ParseVideoInfo(doc.RootElement, url);
    }

    /// <summary>
    /// Resolves the direct CDN URL for a format: returns <see cref="YouTubeFormat.DirectUrl"/> immediately in 0ms
    /// when already present from <see cref="GetVideoInfoAsync"/>, falling back to <c>yt-dlp -g</c> only if missing.
    /// </summary>
    public async Task<string> GetDirectUrlAsync(string videoUrl, YouTubeFormat format, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(format.DirectUrl)) return format.DirectUrl;
        return await GetDirectUrlAsync(videoUrl, format.FormatId, ct).ConfigureAwait(false);
    }

    public async Task<string> GetDirectUrlAsync(string videoUrl, string formatId, CancellationToken ct = default)
    {
        EnsureAvailable();
        var output = await RunProcessAsync(_ytDlpPath!, ["--no-playlist", "--no-warnings", "-f", formatId, "-g", videoUrl], ct).ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines[0];
    }

    /// <summary>
    /// Picks the best <see cref="YouTubeFormat"/> matching a preset key ("best", "2160", "1440", "1080", "720", "480", "360", "audio").
    /// </summary>
    public static YouTubeFormat? PickFormat(YouTubeVideoInfo info, string? presetKey)
    {
        var options = info.QualityOptions.Count > 0 ? info.QualityOptions : info.Formats;
        if (options.Count == 0) return null;

        if (string.Equals(presetKey, "audio", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(f => !f.HasVideo && f.HasAudio)
                ?? info.Formats.FirstOrDefault(f => !f.HasVideo && f.HasAudio)
                ?? options.Last();
        }

        var videoOptions = options.Where(f => f.HasVideo).OrderByDescending(f => f.Height ?? 0).ToList();
        if (videoOptions.Count == 0) return options[0];

        if (int.TryParse(presetKey, out var targetHeight) && targetHeight > 0)
        {
            return videoOptions.FirstOrDefault(f => (f.Height ?? 0) <= targetHeight) ?? videoOptions[^1];
        }

        // "best" or null -> highest available resolution
        return videoOptions[0];
    }

    /// <summary>
    /// Extracts/transcodes audio from <paramref name="inputPartPath"/> into MP3 or M4A at the requested bitrate (in kbps, 0 = stream copy/default).
    /// Replaces <paramref name="inputPartPath"/> atomically so FinalizeAsync moves it to the final .mp3 / .m4a file.
    /// </summary>
    public async Task ConvertToAudioAsync(string inputPartPath, string targetExt, int bitrateKbps, CancellationToken ct = default)
    {
        if (_ffmpegPath is null) return;

        var cleanExt = targetExt.TrimStart('.').ToLowerInvariant();
        if (cleanExt is not ("mp3" or "m4a" or "aac" or "opus" or "ogg" or "wav")) cleanExt = "mp3";

        var outTemp = $"{inputPartPath}.conv.{cleanExt}";
        try
        {
            var args = new List<string> { "-y", "-loglevel", "error", "-i", inputPartPath, "-vn" };
            if (cleanExt == "mp3")
            {
                var br = bitrateKbps is > 0 ? bitrateKbps : 192;
                args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{br}k"]);
            }
            else if (cleanExt == "m4a")
            {
                if (bitrateKbps <= 0)
                {
                    args.AddRange(["-c:a", "copy", "-movflags", "+faststart"]);
                }
                else
                {
                    args.AddRange(["-c:a", "aac", "-b:a", $"{bitrateKbps}k", "-movflags", "+faststart"]);
                }
            }
            else
            {
                args.AddRange(["-c:a", "copy"]);
            }
            args.Add(outTemp);

            try
            {
                await RunProcessAsync(_ffmpegPath, args.ToArray(), ct).ConfigureAwait(false);
            }
            catch when (cleanExt == "m4a" && bitrateKbps <= 0)
            {
                // If input audio codec wasn't AAC (e.g. Opus), transcode cleanly to 192k AAC for .m4a container
                await RunProcessAsync(_ffmpegPath,
                    ["-y", "-loglevel", "error", "-i", inputPartPath, "-vn", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outTemp],
                    ct).ConfigureAwait(false);
            }

            if (File.Exists(outTemp) && new FileInfo(outTemp).Length > 0)
            {
                File.Move(outTemp, inputPartPath, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(outTemp)) File.Delete(outTemp); } catch { }
        }
    }

    /// <summary>
    /// Downloads the companion audio stream and losslessly muxes it into <paramref name="videoPartPath"/> via ffmpeg (-c copy).
    /// </summary>
    public async Task MergeAudioIntoPartAsync(string videoPartPath, string audioUrl, string? userAgent, CancellationToken ct = default)
    {
        if (_ffmpegPath is null) return;

        var audioTemp = videoPartPath + ".audio.m4a";
        var mergedTemp = videoPartPath + ".merged.mp4";
        try
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, audioUrl))
            {
                if (!string.IsNullOrWhiteSpace(userAgent))
                    req.Headers.UserAgent.ParseAdd(userAgent);
                using var resp = await AudioHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(audioTemp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await net.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            await RunProcessAsync(_ffmpegPath,
            [
                "-y", "-loglevel", "error",
                "-i", videoPartPath,
                "-i", audioTemp,
                "-c:v", "copy",
                "-c:a", "aac",
                "-movflags", "+faststart",
                mergedTemp
            ], ct).ConfigureAwait(false);

            if (File.Exists(mergedTemp) && new FileInfo(mergedTemp).Length > 0)
            {
                File.Move(mergedTemp, videoPartPath, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(audioTemp)) File.Delete(audioTemp); } catch { }
            try { if (File.Exists(mergedTemp)) File.Delete(mergedTemp); } catch { }
        }
    }

    // ------------------------------------------------------------------ playlist info

    /// <summary>Gets metadata for all videos in a playlist (flat listing — fast).</summary>
    public async Task<YouTubePlaylistInfo> GetPlaylistInfoAsync(string url, CancellationToken ct = default)
    {
        EnsureAvailable();
        var json = await RunProcessAsync(_ytDlpPath!, ["--flat-playlist", "-J", "--no-warnings", "--no-download", url], ct).ConfigureAwait(false);

        var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = new List<YouTubePlaylistEntry>();
        string? playlistTitle = null;
        string? playlistId = null;
        string? uploader = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("_type", out var t) ? t.GetString() : null;

                if (type is "playlist")
                {
                    playlistTitle = GetString(root, "title");
                    playlistId = GetString(root, "id");
                    uploader = GetString(root, "uploader");
                    if (root.TryGetProperty("entries", out var entriesArray) && entriesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in entriesArray.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object) continue;
                            var videoId = GetString(entry, "id") ?? GetString(entry, "url") ?? "";
                            if (string.IsNullOrWhiteSpace(videoId)) continue;
                            var title = GetString(entry, "title") ?? videoId;
                            var rawUrl = GetString(entry, "url");
                            var videoUrl = rawUrl is not null && rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? rawUrl
                                : $"https://www.youtube.com/watch?v={videoId}";
                            var duration = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                                ? TimeSpan.FromSeconds(d.GetDouble()) : (TimeSpan?)null;
                            entries.Add(new YouTubePlaylistEntry(videoId, title, videoUrl, duration));
                        }
                    }
                }
                else
                {
                    var videoId = GetString(root, "id") ?? GetString(root, "url") ?? "";
                    if (string.IsNullOrWhiteSpace(videoId)) continue;
                    var title = GetString(root, "title") ?? videoId;
                    var rawUrl = GetString(root, "url");
                    var videoUrl = rawUrl is not null && rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? rawUrl
                        : $"https://www.youtube.com/watch?v={videoId}";
                    var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromSeconds(d.GetDouble()) : (TimeSpan?)null;
                    entries.Add(new YouTubePlaylistEntry(videoId, title, videoUrl, duration));

                    playlistTitle ??= GetString(root, "playlist_title");
                    playlistId ??= GetString(root, "playlist_id");
                    uploader ??= GetString(root, "playlist_uploader") ?? GetString(root, "uploader");
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning("Failed to parse yt-dlp playlist JSON line: {Error}", ex.Message);
            }
        }

        return new YouTubePlaylistInfo
        {
            Id = playlistId ?? ExtractPlaylistId(url) ?? "unknown",
            Title = playlistTitle ?? "YouTube Playlist",
            Uploader = uploader,
            Entries = entries,
        };
    }

    // ------------------------------------------------------------------ helpers

    private YouTubeVideoInfo ParseVideoInfo(JsonElement root, string originalUrl)
    {
        var id = GetString(root, "id") ?? "";
        var title = GetString(root, "title") ?? "Unknown";
        var uploader = GetString(root, "uploader");
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(d.GetDouble()) : (TimeSpan?)null;
        var thumbnail = GetString(root, "thumbnail");
        var rootUa = GetUserAgent(root);

        var rawFormats = new List<YouTubeFormat>();

        if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fmts.EnumerateArray())
            {
                var protocol = GetString(f, "protocol") ?? "";
                // Keep direct HTTP(S) progressive/DASH streams (skip HLS/m3u8 fragments & storyboards)
                if (protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
                    protocol.Contains("mhtml", StringComparison.OrdinalIgnoreCase))
                    continue;

                var directUrl = GetString(f, "url");
                if (string.IsNullOrWhiteSpace(directUrl) || !directUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fmtId = GetString(f, "format_id") ?? "unknown";
                if (fmtId.Contains("-drc", StringComparison.OrdinalIgnoreCase)) continue; // skip duplicate DRC tracks

                var label = GetString(f, "format_note") ?? GetString(f, "format") ?? fmtId;
                var ext = GetString(f, "ext");
                if (string.Equals(ext, "mhtml", StringComparison.OrdinalIgnoreCase)) continue;

                var filesize = f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64()
                    : f.TryGetProperty("filesize_approx", out var fsa) && fsa.ValueKind == JsonValueKind.Number ? fsa.GetInt64()
                    : (long?)null;
                var height = f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : (int?)null;
                var width = f.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : (int?)null;
                var abr = f.TryGetProperty("abr", out var ab) && ab.ValueKind == JsonValueKind.Number && ab.GetDouble() > 0
                    ? (int)Math.Round(ab.GetDouble())
                    : f.TryGetProperty("tbr", out var tb) && tb.ValueKind == JsonValueKind.Number && tb.GetDouble() > 0
                        ? (int)Math.Round(tb.GetDouble())
                        : (int?)null;
                var vcodec = GetString(f, "vcodec");
                var acodec = GetString(f, "acodec");
                var ua = GetUserAgent(f) ?? rootUa;

                var hasVideo = vcodec is not null and not "none";
                var hasAudio = acodec is not null and not "none";
                if (!hasVideo && !hasAudio) continue;

                var isCombined = hasVideo && hasAudio;

                rawFormats.Add(new YouTubeFormat(
                    fmtId, label, ext, filesize, height, width, vcodec, acodec,
                    hasVideo, hasAudio, isCombined, directUrl, null, null, ua, abr));
            }
        }

        // Find best audio-only stream (prefer m4a/aac for universal MP4 compatibility, fallback to opus/webm)
        var bestM4aAudio = rawFormats
            .Where(f => !f.HasVideo && f.HasAudio && string.Equals(f.Extension, "m4a", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.FileSizeApprox ?? 0)
            .FirstOrDefault();

        var bestAnyAudio = bestM4aAudio ?? rawFormats
            .Where(f => !f.HasVideo && f.HasAudio)
            .OrderByDescending(f => f.FileSizeApprox ?? 0)
            .FirstOrDefault();

        // Attach companion audio URL to video-only formats when ffmpeg is available
        var enriched = rawFormats.Select(f =>
        {
            if (f.HasVideo && !f.HasAudio && HasFfmpeg && bestAnyAudio?.DirectUrl is not null)
            {
                return f with
                {
                    AudioDirectUrl = bestAnyAudio.DirectUrl,
                    AudioSizeApprox = bestAnyAudio.FileSizeApprox,
                };
            }
            return f;
        }).ToList();

        // Build clean deduplicated VIDEO QualityOptions: one best format per resolution (descending)
        var qualityOptions = new List<YouTubeFormat>();
        var candidateVideos = HasFfmpeg
            ? enriched.Where(f => f.HasVideo && (f.IsCombined || f.NeedsAudioMerge))
            : enriched.Where(f => f.IsCombined);

        foreach (var group in candidateVideos
                     .Where(f => f.Height is > 0)
                     .GroupBy(f => f.Height!.Value)
                     .OrderByDescending(g => g.Key))
        {
            var bestAtHeight = group
                .OrderByDescending(f => f.IsCombined)
                .ThenByDescending(f => string.Equals(f.Extension, "mp4", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.VideoCodec?.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) == true)
                .ThenByDescending(f => f.FileSizeApprox ?? 0)
                .First();
            qualityOptions.Add(bestAtHeight);
        }

        // If a non-YouTube generic video had HasVideo without Height metadata, keep top video stream
        if (qualityOptions.Count == 0)
        {
            var anyVideo = candidateVideos.OrderByDescending(f => f.FileSizeApprox ?? 0).FirstOrDefault();
            if (anyVideo is not null) qualityOptions.Add(anyVideo);
        }

        // Build clean deduplicated AUDIO-ONLY Options (sorted descending by bitrate / size)
        var audioOptions = rawFormats
            .Where(f => !f.HasVideo && f.HasAudio)
            .GroupBy(f => $"{(f.Extension ?? "m4a").ToLowerInvariant()}:{((f.AudioBitrateKbps ?? 128) / 32) * 32}")
            .Select(g => g.OrderByDescending(x => x.FileSizeApprox ?? 0).First())
            .OrderByDescending(f => f.AudioBitrateKbps ?? 0)
            .ThenByDescending(f => f.FileSizeApprox ?? 0)
            .ToList();

        // Keep best audio in QualityOptions too for backwards-compatible callers of PickFormat(info, "audio")
        if (bestM4aAudio is not null)
            qualityOptions.Add(bestM4aAudio);
        else if (bestAnyAudio is not null)
            qualityOptions.Add(bestAnyAudio);

        var sortedAll = enriched
            .OrderByDescending(f => f.IsCombined || f.NeedsAudioMerge)
            .ThenByDescending(f => f.Height ?? 0)
            .ThenByDescending(f => f.FileSizeApprox ?? 0)
            .ToList();

        return new YouTubeVideoInfo
        {
            Id = id,
            Title = title,
            Uploader = uploader,
            Duration = duration,
            ThumbnailUrl = thumbnail,
            Formats = sortedAll,
            QualityOptions = qualityOptions,
            AudioOptions = audioOptions,
            OriginalUrl = originalUrl,
        };
    }

    private static string? GetUserAgent(JsonElement el)
    {
        if (el.TryGetProperty("http_headers", out var hdrs) && hdrs.ValueKind == JsonValueKind.Object)
        {
            if (hdrs.TryGetProperty("User-Agent", out var ua) && ua.ValueKind == JsonValueKind.String)
                return ua.GetString();
        }
        return null;
    }

    private async Task<string> RunProcessAsync(string exePath, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(exePath)}.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = Task.Run(async () =>
        {
            var buffer = new char[8192];
            int read;
            while ((read = await proc.StandardOutput.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                stdout.Append(buffer, 0, read);
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[8192];
            int read;
            while ((read = await proc.StandardError.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                stderr.Append(buffer, 0, read);
        }, ct);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            _log.LogError("{Exe} failed (exit {Code}): {Error}", Path.GetFileName(exePath), proc.ExitCode, err);
            throw new YouTubeException($"{Path.GetFileNameWithoutExtension(exePath)} failed: {(err.Length > 200 ? err[..200] + "…" : err)}");
        }

        return stdout.ToString();
    }

    private void EnsureAvailable()
    {
        if (_ytDlpPath is null)
            throw new YouTubeException("yt-dlp was not found. Install yt-dlp and make sure it is in your PATH.");
    }

    private static string? FindExecutable(string exeName)
    {
        var baseName = Path.GetFileNameWithoutExtension(exeName);

        // 1. Current process PATH + fresh Machine/User PATH
        var paths = new List<string>();
        if (Environment.GetEnvironmentVariable("PATH") is { } p1) paths.AddRange(p1.Split(Path.PathSeparator));
        if (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) is { } p2) paths.AddRange(p2.Split(Path.PathSeparator));
        if (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) is { } p3) paths.AddRange(p3.Split(Path.PathSeparator));

        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* invalid path entry */ }
        }

        // 2. Common install & WinGet locations
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = new[]
        {
            Path.Combine(localApp, "Microsoft", "WinGet", "Links", exeName),
            Path.Combine(localApp, "Nahr", exeName),
            Path.Combine(localApp, baseName, exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), baseName, exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", exeName),
        };
        foreach (var path in common)
        {
            if (File.Exists(path)) return path;
        }

        // 3. Scan WinGet Packages folder if Links shim wasn't created
        var wingetPkgs = Path.Combine(localApp, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPkgs))
        {
            try
            {
                var found = Directory.EnumerateFiles(wingetPkgs, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
            catch { /* ignore access errors */ }
        }

        return null;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Thrown when yt-dlp or ffmpeg fails or is unavailable.</summary>
public sealed class YouTubeException(string message) : Exception(message);
