using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadManager.Engine.YouTube;

/// <summary>
/// Extracts YouTube video &amp; playlist metadata and direct CDN stream URLs using a fast built-in C#
/// InnerTube client (immune to Windows Application Control blocking unsigned external tools), with
/// optional yt-dlp fallback for non-YouTube sites and ffmpeg for lossless high-res DASH audio muxing.
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
    private static readonly HttpClient ApiHttp = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly (string Name, string Version, string UserAgent)[] InnerTubePlayerClients =
    [
        ("ANDROID_VR", "1.65.10", "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip"),
        ("IOS", "20.10.4", "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)"),
        ("ANDROID", "20.10.38", "com.google.android.youtube/20.10.38 (Linux; U; Android 14) gzip"),
    ];

    private const string WebBrowseClientVersion = "2.20261119.01.00";
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly string? _ytDlpPath;
    private readonly string? _ffmpegPath;
    private readonly ILogger _log;
    private volatile bool _ytDlpBlocked;

    public YouTubeService(ILogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
        _ytDlpPath = FindExecutable("yt-dlp.exe");
        _ffmpegPath = FindExecutable("ffmpeg.exe");
    }

    /// <summary>Whether yt-dlp was found on this machine (for non-YouTube sites; YouTube works natively without yt-dlp).</summary>
    public bool IsAvailable => _ytDlpPath is not null;

    /// <summary>Whether yt-dlp can be executed (present and not blocked by Windows Application Control).</summary>
    public bool CanRunYtDlp => _ytDlpPath is not null && !_ytDlpBlocked;

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

    /// <summary>Gets metadata, clean quality options, and direct CDN URLs (native InnerTube first for YouTube, yt-dlp fallback).</summary>
    public async Task<YouTubeVideoInfo> GetVideoInfoAsync(string url, CancellationToken ct = default)
    {
        Exception? nativeError = null;
        if (IsYouTubeUrl(url) && ExtractVideoId(url) is { } videoId)
        {
            try
            {
                var nativeInfo = await GetVideoInfoNativeAsync(videoId, url, ct).ConfigureAwait(false);
                if (nativeInfo.Formats.Count > 0) return nativeInfo;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                nativeError = ex;
                _log.LogWarning("Native YouTube video extraction failed for {Id}: {Error}", videoId, ex.Message);
            }
        }

        if (CanRunYtDlp)
        {
            var json = await RunProcessAsync(_ytDlpPath!, ["-J", "--no-playlist", "--no-warnings", "--no-download", url], ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return ParseVideoInfo(doc.RootElement, url);
        }

        if (nativeError is not null)
            throw new YouTubeException(nativeError.Message);

        EnsureAvailable();
        throw new YouTubeException("Could not extract video info.");
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

    /// <summary>Gets metadata for all videos in a playlist (native YouTube InnerTube browse first, yt-dlp fallback).</summary>
    public async Task<YouTubePlaylistInfo> GetPlaylistInfoAsync(string url, CancellationToken ct = default)
    {
        Exception? nativeError = null;
        if (IsYouTubeUrl(url) && ExtractPlaylistId(url) is { } plId)
        {
            try
            {
                var nativePlaylist = await GetPlaylistInfoNativeAsync(plId, ct).ConfigureAwait(false);
                if (nativePlaylist.Entries.Count > 0) return nativePlaylist;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                nativeError = ex;
                _log.LogWarning("Native YouTube playlist extraction failed for {Id}: {Error}", plId, ex.Message);
            }
        }

        if (!CanRunYtDlp)
        {
            if (nativeError is not null) throw new YouTubeException(nativeError.Message);
            throw new YouTubeException("No videos were found in this YouTube playlist (it may be private, empty, or unavailable).");
        }

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

    // ------------------------------------------------------------------ built-in pure-C# YouTube InnerTube extractor

    private async Task<YouTubePlaylistInfo> GetPlaylistInfoNativeAsync(string playlistId, CancellationToken ct)
    {
        var entries = new List<YouTubePlaylistEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        string? title = null;
        string? uploader = null;
        string? continuation = null;

        // Fetch initial page + up to 9 continuation pages (~1000 videos max)
        for (var page = 0; page < 10; page++)
        {
            object payload = continuation is null
                ? new
                {
                    browseId = playlistId.StartsWith("VL", StringComparison.OrdinalIgnoreCase) ? playlistId : "VL" + playlistId,
                    context = new
                    {
                        client = new
                        {
                            clientName = "WEB",
                            clientVersion = WebBrowseClientVersion,
                            hl = "en",
                            gl = "US",
                        }
                    }
                }
                : new
                {
                    continuation,
                    context = new
                    {
                        client = new
                        {
                            clientName = "WEB",
                            clientVersion = WebBrowseClientVersion,
                            hl = "en",
                            gl = "US",
                        }
                    }
                };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var resp = await ApiHttp.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var parsed = ParseNativePlaylistBrowseJson(body, playlistId);
            title ??= parsed.Title;
            uploader ??= parsed.Uploader;

            var addedThisPage = 0;
            foreach (var entry in parsed.Entries)
            {
                if (seenIds.Add(entry.VideoId))
                {
                    entries.Add(entry);
                    addedThisPage++;
                }
            }

            if (string.IsNullOrWhiteSpace(parsed.ContinuationToken) || addedThisPage == 0)
                break;

            continuation = parsed.ContinuationToken;
        }

        return new YouTubePlaylistInfo
        {
            Id = playlistId,
            Title = string.IsNullOrWhiteSpace(title) ? "YouTube Playlist" : title!,
            Uploader = uploader,
            Entries = entries,
        };
    }

    /// <summary>Parses a YouTube InnerTube `/youtubei/v1/browse` JSON response (supports both 2026 lockupViewModel and playlistVideoRenderer).</summary>
    internal static (string? Title, string? Uploader, List<YouTubePlaylistEntry> Entries, string? ContinuationToken)
        ParseNativePlaylistBrowseJson(string json, string playlistId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? title = null;
        string? uploader = null;
        if (root.TryGetProperty("metadata", out var meta)
            && meta.TryGetProperty("playlistMetadataRenderer", out var pmr))
        {
            title = GetString(pmr, "title");
        }

        var entries = new List<YouTubePlaylistEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
                return;
            }
            if (el.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name == "playlistVideoRenderer" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var v = prop.Value;
                    var vid = GetString(v, "videoId");
                    if (!string.IsNullOrWhiteSpace(vid) && seen.Add(vid!))
                    {
                        var vTitle = ReadTextFromRunsOrSimple(v, "title") ?? vid!;
                        TimeSpan? dur = null;
                        if (GetString(v, "lengthSeconds") is { } ls && double.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                            dur = TimeSpan.FromSeconds(secs);
                        else
                            dur = ParseDurationText(ReadTextFromRunsOrSimple(v, "lengthText"));

                        entries.Add(new YouTubePlaylistEntry(vid!, vTitle, $"https://www.youtube.com/watch?v={vid}", dur));
                    }
                    continue;
                }

                if (prop.Name == "lockupViewModel" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var lm = prop.Value;
                    var vid = GetString(lm, "contentId");
                    if (!string.IsNullOrWhiteSpace(vid) && vid!.Length == 11 && seen.Add(vid))
                    {
                        string? vTitle = null;
                        if (lm.TryGetProperty("metadata", out var lmMeta)
                            && lmMeta.TryGetProperty("lockupMetadataViewModel", out var lmvm)
                            && lmvm.TryGetProperty("title", out var tEl))
                        {
                            vTitle = GetString(tEl, "content");
                        }
                        var dur = ExtractLockupDuration(lm);
                        entries.Add(new YouTubePlaylistEntry(vid, string.IsNullOrWhiteSpace(vTitle) ? vid : vTitle!, $"https://www.youtube.com/watch?v={vid}", dur));
                    }
                    continue;
                }

                if (prop.Name == "pageHeaderViewModel" && title is null && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("title", out var phTitle)
                        && phTitle.TryGetProperty("dynamicTextViewModel", out var dtvm)
                        && dtvm.TryGetProperty("text", out var txt))
                    {
                        title = GetString(txt, "content");
                    }
                }

                if (prop.Name == "continuationCommand" && continuationToken is null && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var tok = GetString(prop.Value, "token");
                    if (!string.IsNullOrWhiteSpace(tok)) continuationToken = tok;
                }

                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    Walk(prop.Value);
            }
        }

        Walk(root);
        return (title, uploader, entries, continuationToken);
    }

    private async Task<YouTubeVideoInfo> GetVideoInfoNativeAsync(string videoId, string originalUrl, CancellationToken ct)
    {
        string? lastReason = null;

        foreach (var client in InnerTubePlayerClients)
        {
            var payload = new
            {
                videoId,
                contentCheckOk = true,
                racyCheckOk = true,
                context = new
                {
                    client = new
                    {
                        clientName = client.Name,
                        clientVersion = client.Version,
                        hl = "en",
                        gl = "US",
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var resp = await ApiHttp.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) continue;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseNativePlayerJson(body, originalUrl, client.UserAgent, HasFfmpeg, out var reason);
            if (parsed is not null && parsed.Formats.Count > 0)
                return parsed;

            if (!string.IsNullOrWhiteSpace(reason)) lastReason = reason;
        }

        throw new YouTubeException(lastReason ?? "Could not extract playable formats for this YouTube video.");
    }

    /// <summary>Parses a YouTube InnerTube `/youtubei/v1/player` JSON response into a <see cref="YouTubeVideoInfo"/>.</summary>
    internal static YouTubeVideoInfo? ParseNativePlayerJson(string json, string originalUrl, string userAgent, bool hasFfmpeg, out string? errorReason)
    {
        errorReason = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("playabilityStatus", out var ps))
        {
            var status = GetString(ps, "status");
            if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                errorReason = GetString(ps, "reason") ?? status;
                return null;
            }
        }

        string id = ExtractVideoId(originalUrl) ?? "";
        string title = "Unknown";
        string? uploader = null;
        TimeSpan? duration = null;
        string? thumbnail = null;

        if (root.TryGetProperty("videoDetails", out var vd))
        {
            id = GetString(vd, "videoId") ?? id;
            title = GetString(vd, "title") ?? title;
            uploader = GetString(vd, "author");
            if (GetString(vd, "lengthSeconds") is { } ls && double.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                duration = TimeSpan.FromSeconds(secs);
            if (vd.TryGetProperty("thumbnail", out var th)
                && th.TryGetProperty("thumbnails", out var thArr)
                && thArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in thArr.EnumerateArray())
                    thumbnail = GetString(t, "url") ?? thumbnail;
            }
        }

        var rawFormats = new List<YouTubeFormat>();
        if (root.TryGetProperty("streamingData", out var sd))
        {
            void AddArray(string propName)
            {
                if (!sd.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                foreach (var f in arr.EnumerateArray())
                {
                    var directUrl = GetString(f, "url");
                    if (string.IsNullOrWhiteSpace(directUrl) || !directUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var itag = f.TryGetProperty("itag", out var itEl) && itEl.ValueKind == JsonValueKind.Number
                        ? itEl.GetInt32().ToString(CultureInfo.InvariantCulture)
                        : "unknown";
                    var mime = GetString(f, "mimeType") ?? "";
                    var (ext, vcodec, acodec, hasVideo, hasAudio) = ParseMimeType(mime);
                    if (!hasVideo && !hasAudio) continue;

                    long? filesize = null;
                    if (GetString(f, "contentLength") is { } clStr && long.TryParse(clStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clVal) && clVal > 0)
                        filesize = clVal;

                    int? height = f.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number ? hEl.GetInt32() : null;
                    int? width = f.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetInt32() : null;

                    int? bitrateKbps = null;
                    if (f.TryGetProperty("averageBitrate", out var abEl) && abEl.ValueKind == JsonValueKind.Number && abEl.GetInt64() > 0)
                        bitrateKbps = (int)Math.Round(abEl.GetInt64() / 1000.0);
                    else if (f.TryGetProperty("bitrate", out var bEl) && bEl.ValueKind == JsonValueKind.Number && bEl.GetInt64() > 0)
                        bitrateKbps = (int)Math.Round(bEl.GetInt64() / 1000.0);

                    // Approximate size from bitrate * duration if contentLength was omitted
                    if (filesize is null && bitrateKbps is > 0 && duration is { TotalSeconds: > 0 })
                        filesize = (long)(bitrateKbps.Value * 1000.0 / 8.0 * duration.Value.TotalSeconds);

                    var label = GetString(f, "qualityLabel") ?? GetString(f, "quality") ?? itag;
                    var isCombined = hasVideo && hasAudio;

                    rawFormats.Add(new YouTubeFormat(
                        itag, label, ext, filesize, height, width, vcodec, acodec,
                        hasVideo, hasAudio, isCombined, directUrl, null, null, userAgent, bitrateKbps));
                }
            }

            AddArray("formats");
            AddArray("adaptiveFormats");
        }

        return BuildVideoInfo(id, title, uploader, duration, thumbnail, originalUrl, rawFormats, hasFfmpeg);
    }

    private static (string Extension, string? VCodec, string? ACodec, bool HasVideo, bool HasAudio) ParseMimeType(string mimeType)
    {
        // e.g. "video/mp4; codecs=\"avc1.640028\"" or "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"" or "audio/mp4; codecs=\"mp4a.40.2\""
        var isVideoMime = mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var isAudioMime = mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        var ext = "mp4";
        if (mimeType.Contains("webm", StringComparison.OrdinalIgnoreCase)) ext = "webm";
        else if (isAudioMime && mimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase)) ext = "m4a";
        else if (mimeType.Contains("3gpp", StringComparison.OrdinalIgnoreCase)) ext = "3gp";

        string? vcodec = null;
        string? acodec = null;
        var cm = Regex.Match(mimeType, @"codecs=""([^""]+)""", RegexOptions.IgnoreCase);
        if (cm.Success)
        {
            var parts = cm.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (isVideoMime)
            {
                if (parts.Length >= 1) vcodec = parts[0];
                if (parts.Length >= 2) acodec = parts[1];
            }
            else if (isAudioMime)
            {
                if (parts.Length >= 1) acodec = parts[0];
            }
        }
        else
        {
            if (isVideoMime) vcodec = "unknown";
            if (isAudioMime) acodec = "unknown";
        }

        return (ext, vcodec, acodec, vcodec is not null, acodec is not null);
    }

    private static string? ReadTextFromRunsOrSimple(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        if (GetString(el, "simpleText") is { } simple) return simple;
        if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray())
            {
                if (GetString(r, "text") is { } txt) sb.Append(txt);
            }
            if (sb.Length > 0) return sb.ToString();
        }
        return null;
    }

    private static TimeSpan? ExtractLockupDuration(JsonElement lockup)
    {
        if (!lockup.TryGetProperty("contentImage", out var ci)) return null;
        if (!ci.TryGetProperty("thumbnailViewModel", out var tvm)) return null;
        if (!tvm.TryGetProperty("overlays", out var overlays) || overlays.ValueKind != JsonValueKind.Array) return null;

        foreach (var ov in overlays.EnumerateArray())
        {
            if (ov.TryGetProperty("thumbnailBottomOverlayViewModel", out var bottom)
                && bottom.TryGetProperty("badges", out var badges)
                && badges.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in badges.EnumerateArray())
                {
                    if (b.TryGetProperty("thumbnailBadgeViewModel", out var bvm)
                        && GetString(bvm, "text") is { } txt)
                    {
                        var parsed = ParseDurationText(txt);
                        if (parsed is not null) return parsed;
                    }
                }
            }
        }
        return null;
    }

    internal static TimeSpan? ParseDurationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            return new TimeSpan(0, m, s);
        }
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m3)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s3))
        {
            return new TimeSpan(h, m3, s3);
        }
        return null;
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

        return BuildVideoInfo(id, title, uploader, duration, thumbnail, originalUrl, rawFormats, HasFfmpeg);
    }

    private static YouTubeVideoInfo BuildVideoInfo(
        string id,
        string title,
        string? uploader,
        TimeSpan? duration,
        string? thumbnail,
        string originalUrl,
        List<YouTubeFormat> rawFormats,
        bool hasFfmpeg)
    {
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
            if (f.HasVideo && !f.HasAudio && hasFfmpeg && bestAnyAudio?.DirectUrl is not null)
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
        var candidateVideos = hasFfmpeg
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

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            if (string.Equals(exePath, _ytDlpPath, StringComparison.OrdinalIgnoreCase))
                _ytDlpBlocked = true;
            _log.LogWarning("Could not start {Exe} (NativeError={Code}): {Msg}", Path.GetFileName(exePath), ex.NativeErrorCode, ex.Message);
            throw new YouTubeException($"External tool {Path.GetFileName(exePath)} could not be started ({ex.Message}).");
        }
        if (proc is null) throw new InvalidOperationException($"Failed to start {Path.GetFileName(exePath)}.");
        using var _ = proc;

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
