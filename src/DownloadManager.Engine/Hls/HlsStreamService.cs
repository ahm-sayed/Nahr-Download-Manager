using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Core.Models;

namespace DownloadManager.Engine.Hls;

public sealed record HlsVariant(
    string Label,
    string Url,
    int? Height,
    int? Width,
    long? Bandwidth,
    string? AudioUrl = null);

public sealed record HlsSegment(
    int Index,
    string Url,
    double DurationSeconds,
    string? KeyUrl,
    byte[]? ExplicitIv);

/// <summary>
/// Parses HLS (.m3u8 / #EXTM3U) master &amp; media playlists (including disguised extension URLs on streaming CDNs),
/// downloads segments in parallel with AES-128 support, and remuxes into a playable .mp4 via ffmpeg.
/// </summary>
public static class HlsStreamService
{
    private static readonly Regex AttrRegex = new(@"([A-Z0-9\-]+)=(?:""([^""]*)""|([^,\s]*))", RegexOptions.Compiled);
    private static readonly Regex UrlResolutionTokenRegex = new(
        @"(?<=[\/_\-\.\?=])(2160|1440|1080|720|576|540|480|360|240)(p)?(?=[\/_\-\.\?&]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly int[] StandardHeights = [2160, 1440, 1080, 720, 480, 360];

    public static bool LooksLikeHlsUrl(string? url, string? mediaType = null)
    {
        if (string.Equals(mediaType, "hls", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Regex.IsMatch(url, @"\.m3u8(\?|#|$)", RegexOptions.IgnoreCase) ||
               url.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/m3u8/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/hls/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts a video resolution height (e.g. 1080, 720, 480, 360) from a URL or label when present
    /// (such as ".../2/1080p/playlist.m3u8" or ".../video_720p.m3u8").
    /// </summary>
    public static int? TryExtractHeightFromUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var matches = UrlResolutionTokenRegex.Matches(text);
        if (matches.Count == 0) return null;
        // Prefer a match with explicit 'p' suffix (e.g. "1080p"), otherwise use the last resolution segment in the path
        foreach (Match m in matches)
        {
            if (m.Groups[2].Success && int.TryParse(m.Groups[1].Value, out var hp))
                return hp;
        }
        return int.TryParse(matches[^1].Groups[1].Value, out var h) ? h : null;
    }

    public static string FormatResolutionHead(int height)
    {
        var badge = height switch
        {
            >= 2160 => " 4K",
            >= 1440 => " 2K",
            1080 => " Full HD",
            720 => " HD",
            _ => "",
        };
        return $"{height}p{badge}";
    }

    /// <summary>
    /// Fetches an HLS URL and checks if it is a master playlist (multiple resolutions) or a single media playlist.
    /// If it is a single-resolution media playlist whose URL contains a resolution token (e.g. "/1080p/playlist.m3u8"),
    /// automatically probes parent master playlist candidates and sibling resolution URLs (1080p/720p/480p/360p) in parallel.
    /// </summary>
    public static async Task<IReadOnlyList<HlsVariant>> ProbeVariantsAsync(
        HttpClient http,
        string url,
        string? referer,
        string? origin,
        string? userAgent,
        CancellationToken ct = default)
    {
        var (text, finalUri) = await FetchTextAsync(http, url, referer, origin, userAgent, ct).ConfigureAwait(false);
        if (!text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            return [];

        if (text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMasterVariants(text, finalUri);
        }

        // Single-stream media playlist — extract resolution from URL and estimate duration
        var totalSec = EstimateTotalDuration(text);
        var durText = totalSec > 60 ? $" · {(int)(totalSec / 60)} min" : "";
        var detectedHeight = TryExtractHeightFromUrl(finalUri.AbsoluteUri) ?? TryExtractHeightFromUrl(url);
        var selfHead = detectedHeight is > 0 ? FormatResolutionHead(detectedHeight.Value) : "Full Stream";
        var selfVariant = new HlsVariant($"{selfHead} (MP4{durText})", finalUri.AbsoluteUri, detectedHeight, null, null);

        // If the URL has a resolution token (like ".../2/1080p/playlist.m3u8"), discover parent master or sibling qualities!
        var discovered = await TryDiscoverSiblingOrParentVariantsAsync(
            http, finalUri, detectedHeight, durText, referer, origin, userAgent, ct).ConfigureAwait(false);
        if (discovered.Count > 1)
            return discovered;

        return [selfVariant];
    }

    private static IReadOnlyList<HlsVariant> ParseMasterVariants(string text, Uri finalUri)
    {
        // Parse #EXT-X-MEDIA:TYPE=AUDIO groups if any
        var audioGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)) continue;
            var attrs = ParseAttributes(line["#EXT-X-MEDIA:".Length..]);
            if (attrs.TryGetValue("TYPE", out var mtype) && string.Equals(mtype, "AUDIO", StringComparison.OrdinalIgnoreCase) &&
                attrs.TryGetValue("GROUP-ID", out var gid) && attrs.TryGetValue("URI", out var auri) && !string.IsNullOrWhiteSpace(auri))
            {
                var resolvedAudio = new Uri(finalUri, auri).AbsoluteUri;
                if (!audioGroups.ContainsKey(gid) || (attrs.TryGetValue("DEFAULT", out var def) && string.Equals(def, "YES", StringComparison.OrdinalIgnoreCase)))
                {
                    audioGroups[gid] = resolvedAudio;
                }
            }
        }

        var variants = new List<HlsVariant>();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)) continue;

            var attrs = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
            int? width = null, height = null;
            if (attrs.TryGetValue("RESOLUTION", out var res))
            {
                var parts = res.Split('x', 'X');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                {
                    width = w;
                    height = h;
                }
            }

            long? bw = attrs.TryGetValue("BANDWIDTH", out var bws) && long.TryParse(bws, out var bval) ? bval : null;
            string? audioUrl = attrs.TryGetValue("AUDIO", out var audioGroup) && audioGroups.TryGetValue(audioGroup, out var au) ? au : null;

            // Next non-comment line is the variant URI
            string? nextUri = null;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (!lines[j].StartsWith('#'))
                {
                    nextUri = lines[j];
                    i = j;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(nextUri)) continue;

            var resolved = new Uri(finalUri, nextUri).AbsoluteUri;
            height ??= TryExtractHeightFromUrl(attrs.GetValueOrDefault("NAME")) ?? TryExtractHeightFromUrl(resolved);
            var label = BuildVariantLabel(height, bw, attrs.GetValueOrDefault("NAME"));
            variants.Add(new HlsVariant(label, resolved, height, width, bw, audioUrl));
        }

        // Deduplicate by height (keep highest bandwidth per resolution) and sort descending
        var deduped = variants
            .GroupBy(v => v.Height ?? (int)(v.Bandwidth ?? (variants.IndexOf(v) + 1)))
            .Select(g => g.OrderByDescending(x => x.Bandwidth ?? 0).First())
            .OrderByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.Bandwidth ?? 0)
            .ToList();

        return deduped.Count > 0 ? deduped : [new HlsVariant("Best Stream (MP4)", finalUri.AbsoluteUri, null, null, null)];
    }

    private static async Task<IReadOnlyList<HlsVariant>> TryDiscoverSiblingOrParentVariantsAsync(
        HttpClient http,
        Uri singleVariantUri,
        int? selfHeight,
        string durText,
        string? referer,
        string? origin,
        string? userAgent,
        CancellationToken outerCt)
    {
        var abs = singleVariantUri.AbsoluteUri;
        var matches = UrlResolutionTokenRegex.Matches(abs);
        if (matches.Count == 0 || selfHeight is null) return [];

        // Pick the resolution token match (prefer the one with 'p' like "1080p")
        Match tokenMatch = matches[^1];
        foreach (Match m in matches)
        {
            if (m.Groups[2].Success) tokenMatch = m;
        }

        var hasPSuffix = tokenMatch.Groups[2].Success;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
        var probeCt = timeoutCts.Token;

        // 1. Check if a parent master.m3u8 / playlist.m3u8 exists right above the resolution folder (e.g. ".../2/master.m3u8")
        var beforeToken = abs[..tokenMatch.Index];
        var lastSlash = beforeToken.LastIndexOf('/');
        if (lastSlash > 8)
        {
            var parentBase = beforeToken[..(lastSlash + 1)];
            var query = singleVariantUri.Query;
            foreach (var masterName in new[] { "master.m3u8", "playlist.m3u8", "index.m3u8" })
            {
                var candidateMaster = parentBase + masterName + query;
                if (string.Equals(candidateMaster, abs, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var (mText, mUri) = await FetchTextAsync(http, candidateMaster, referer, origin, userAgent, probeCt).ConfigureAwait(false);
                    if (mText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                    {
                        var parsed = ParseMasterVariants(mText, mUri);
                        if (parsed.Count > 1) return parsed;
                    }
                }
                catch { /* try next */ }
            }
        }

        // 2. Probe sibling resolution URLs in parallel (e.g. substituting "1080p" -> "2160p", "1440p", "720p", "480p", "360p")
        var selfVariant = new HlsVariant($"{FormatResolutionHead(selfHeight.Value)} (MP4{durText})", abs, selfHeight, null, null);
        var tasks = StandardHeights
            .Where(h => h != selfHeight.Value)
            .Select(async h =>
            {
                var replacement = hasPSuffix ? $"{h}p" : $"{h}";
                var siblingUrl = abs[..tokenMatch.Index] + replacement + abs[(tokenMatch.Index + tokenMatch.Length)..];
                try
                {
                    var (sText, sUri) = await FetchTextAsync(http, siblingUrl, referer, origin, userAgent, probeCt).ConfigureAwait(false);
                    if (sText.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase) &&
                        sText.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HlsVariant($"{FormatResolutionHead(h)} (MP4{durText})", sUri.AbsoluteUri, h, null, null);
                    }
                }
                catch { /* sibling height not available on CDN */ }
                return null;
            });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var all = results.Where(v => v is not null).Cast<HlsVariant>().Append(selfVariant)
            .OrderByDescending(v => v.Height ?? 0)
            .ToList();
        return all;
    }

    /// <summary>
    /// Downloads all segments of an HLS variant playlist in parallel using <paramref name="connections"/> workers,
    /// reports live byte/percent/speed/ETA progress via <paramref name="onProgress"/>, and remuxes into <paramref name="outputMp4Path"/>.
    /// </summary>
    public static async Task DownloadHlsToMp4Async(
        HttpClient http,
        string playlistUrl,
        string outputMp4Path,
        int connections,
        string? referer,
        string? origin,
        string? userAgent,
        string? companionAudioPlaylistUrl,
        string? ffmpegPath,
        Action<long, long, double, TimeSpan?, IReadOnlyList<SegmentInfo>> onProgress,
        CancellationToken ct,
        bool audioOnly = false,
        string audioExt = "mp3",
        int audioBitrateKbps = 192)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "nahr-hls-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(workDir);

        try
        {
            var primarySourceUrl = audioOnly && !string.IsNullOrWhiteSpace(companionAudioPlaylistUrl)
                ? companionAudioPlaylistUrl!
                : playlistUrl;

            var rawVideoPath = Path.Combine(workDir, "video.stream");
            await DownloadMediaPlaylistToFileAsync(
                http, primarySourceUrl, rawVideoPath, connections, referer, origin, userAgent, onProgress, ct).ConfigureAwait(false);

            string? rawAudioPath = null;
            if (!audioOnly && !string.IsNullOrWhiteSpace(companionAudioPlaylistUrl))
            {
                rawAudioPath = Path.Combine(workDir, "audio.stream");
                await DownloadMediaPlaylistToFileAsync(
                    http, companionAudioPlaylistUrl!, rawAudioPath, Math.Max(2, connections / 2), referer, origin, userAgent, null, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputMp4Path)!);

            if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
            {
                var cleanAudioExt = audioExt.TrimStart('.').ToLowerInvariant();
                var remuxExt = audioOnly ? (cleanAudioExt is "m4a" ? "m4a" : "mp3") : "mp4";
                var mergedTemp = $"{outputMp4Path}.remux.{remuxExt}";
                try
                {
                    if (audioOnly)
                    {
                        var br = audioBitrateKbps > 0 ? audioBitrateKbps : 192;
                        var audioArgs = remuxExt == "m4a"
                            ? new List<string> { "-y", "-loglevel", "error", "-i", rawVideoPath, "-vn", "-c:a", "aac", "-b:a", $"{br}k", "-movflags", "+faststart", mergedTemp }
                            : new List<string> { "-y", "-loglevel", "error", "-i", rawVideoPath, "-vn", "-c:a", "libmp3lame", "-b:a", $"{br}k", mergedTemp };

                        var audioOk = await TryRunFfmpegAsync(ffmpegPath!, audioArgs, ct).ConfigureAwait(false);
                        if (audioOk && File.Exists(mergedTemp) && new FileInfo(mergedTemp).Length > 0)
                        {
                            File.Move(mergedTemp, outputMp4Path, overwrite: true);
                            return;
                        }
                    }
                    else
                    {
                        var args = new List<string> { "-y", "-loglevel", "error", "-i", rawVideoPath };
                        if (rawAudioPath is not null && File.Exists(rawAudioPath) && new FileInfo(rawAudioPath).Length > 0)
                        {
                            args.AddRange(["-i", rawAudioPath, "-c:v", "copy", "-c:a", "aac"]);
                        }
                        else
                        {
                            args.AddRange(["-c", "copy", "-bsf:a", "aac_adtstoasc"]);
                        }
                        args.AddRange(["-movflags", "+faststart", mergedTemp]);

                        var ok = await TryRunFfmpegAsync(ffmpegPath!, args, ct).ConfigureAwait(false);
                        if (!ok)
                        {
                            var fallbackArgs = new List<string> { "-y", "-loglevel", "error", "-i", rawVideoPath };
                            if (rawAudioPath is not null && File.Exists(rawAudioPath))
                                fallbackArgs.AddRange(["-i", rawAudioPath]);
                            fallbackArgs.AddRange(["-c", "copy", mergedTemp]);
                            ok = await TryRunFfmpegAsync(ffmpegPath!, fallbackArgs, ct).ConfigureAwait(false);
                        }

                        if (ok && File.Exists(mergedTemp) && new FileInfo(mergedTemp).Length > 0)
                        {
                            File.Move(mergedTemp, outputMp4Path, overwrite: true);
                            return;
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(mergedTemp)) File.Delete(mergedTemp); } catch { }
                }
            }

            // Fallback if ffmpeg unavailable: move concatenated stream directly
            File.Move(rawVideoPath, outputMp4Path, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static async Task DownloadMediaPlaylistToFileAsync(
        HttpClient http,
        string playlistUrl,
        string outputStreamPath,
        int connections,
        string? referer,
        string? origin,
        string? userAgent,
        Action<long, long, double, TimeSpan?, IReadOnlyList<SegmentInfo>>? onProgress,
        CancellationToken ct)
    {
        var (text, baseUri) = await FetchTextAsync(http, playlistUrl, referer, origin, userAgent, ct).ConfigureAwait(false);

        // If caller passed a master playlist directly without probing first, pick the best variant automatically
        if (text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            var variants = await ProbeVariantsAsync(http, playlistUrl, referer, origin, userAgent, ct).ConfigureAwait(false);
            if (variants.Count == 0) throw new InvalidOperationException("No playable HLS variants found in master playlist.");
            (text, baseUri) = await FetchTextAsync(http, variants[0].Url, referer, origin, userAgent, ct).ConfigureAwait(false);
        }

        var (initMapUrl, initKeyUrl, initIv, segments) = ParseMediaSegments(text, baseUri);
        if (segments.Count == 0)
            throw new InvalidOperationException("HLS playlist contained zero media segments.");

        var segDir = outputStreamPath + ".parts";
        Directory.CreateDirectory(segDir);

        var keyCache = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        async ValueTask<byte[]> GetKeyAsync(string kUrl)
        {
            if (keyCache.TryGetValue(kUrl, out var cached)) return cached;
            var bytes = await FetchBytesAsync(http, kUrl, referer, origin, userAgent, ct).ConfigureAwait(false);
            keyCache[kUrl] = bytes;
            return bytes;
        }

        byte[]? initSegmentBytes = null;
        if (!string.IsNullOrWhiteSpace(initMapUrl))
        {
            var rawInit = await FetchBytesAsync(http, initMapUrl!, referer, origin, userAgent, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(initKeyUrl))
            {
                var k = await GetKeyAsync(initKeyUrl!).ConfigureAwait(false);
                rawInit = DecryptAes128(rawInit, k, initIv, 0);
            }
            initSegmentBytes = rawInit;
        }

        var workerCount = Math.Clamp(connections, 1, Math.Min(32, segments.Count));
        var meter = new SpeedMeter();
        meter.Start();

        long downloadedBytes = initSegmentBytes?.Length ?? 0;
        long completedSegmentBytes = 0;
        var completedCount = 0;
        var totalSegs = segments.Count;

        // Represent each worker slot as a visual SegmentInfo row so the UI shows parallel connections working!
        var workerRows = Enumerable.Range(0, workerCount)
            .Select(i => new SegmentInfo { Index = i, Start = 0, End = 999, Downloaded = 0, ActualBytes = 0 })
            .ToArray();

        var reportLock = new object();

        void ReportProgress()
        {
            if (onProgress is null) return;
            lock (reportLock)
            {
                var doneSegs = Volatile.Read(ref completedCount);
                var doneSegBytes = Volatile.Read(ref completedSegmentBytes);
                var bytesSoFar = Volatile.Read(ref downloadedBytes);
                var initLen = initSegmentBytes?.Length ?? 0;

                // Estimate total strictly from completed segments' average size so live mid-segment bytes
                // don't distort or bounce the estimated file size and ETA.
                long estimatedTotal;
                if (doneSegs >= totalSegs)
                {
                    estimatedTotal = bytesSoFar;
                }
                else if (doneSegs > 0)
                {
                    var avgPerSeg = (double)doneSegBytes / doneSegs;
                    estimatedTotal = Math.Max(bytesSoFar, initLen + (long)(avgPerSeg * totalSegs));
                }
                else
                {
                    estimatedTotal = Math.Max(bytesSoFar, totalSegs * 512L * 1024L);
                }

                var speed = meter.CurrentBytesPerSecond;
                TimeSpan? eta = SpeedMeter.EstimateRemaining(estimatedTotal - bytesSoFar, speed);

                var snapshots = workerRows.Select(w => w.Clone()).ToList();
                onProgress(bytesSoFar, estimatedTotal, speed, eta, snapshots);
            }
        }

        var queue = new System.Collections.Concurrent.ConcurrentQueue<HlsSegment>(segments);
        var expectedPerWorker = Math.Max(1, (totalSegs + workerCount - 1) / workerCount);
        for (var i = 0; i < workerCount; i++)
        {
            workerRows[i].Start = i * expectedPerWorker * 1000L;
            workerRows[i].End = workerRows[i].Start + expectedPerWorker * 1000L - 1;
            workerRows[i].ActualBytes = 0;
        }

        // Smooth 100ms live telemetry loop while workers stream segment chunks in parallel
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tickerTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try
            {
                while (await timer.WaitForNextTickAsync(tickerCts.Token).ConfigureAwait(false))
                    ReportProgress();
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        var tasks = Enumerable.Range(0, workerCount).Select(async workerIdx =>
        {
            var row = workerRows[workerIdx];
            var assignedDone = 0;
            long workerBytes = 0;

            while (queue.TryDequeue(out var seg))
            {
                ct.ThrowIfCancellationRequested();
                long inFlightForSeg = 0;
                byte[] data = await FetchWithRetryAsync(
                    http, seg.Url, referer, origin, userAgent, ct,
                    onChunkRead: chunk =>
                    {
                        inFlightForSeg += chunk;
                        Interlocked.Add(ref downloadedBytes, chunk);
                        if (chunk > 0) meter.Add(chunk);
                        workerBytes += chunk;
                        row.ActualBytes = Math.Max(0, workerBytes);
                        // Advance row progress smoothly within the current segment slot
                        var avgSeg = Volatile.Read(ref completedCount) > 0
                            ? Math.Max(65536L, Volatile.Read(ref completedSegmentBytes) / Volatile.Read(ref completedCount))
                            : 512L * 1024L;
                        var partialSlot = Math.Min(950L, (long)(Math.Max(0, inFlightForSeg) * 950.0 / avgSeg));
                        row.Downloaded = Math.Min(row.Length, assignedDone * 1000L + partialSlot);
                    },
                    onAttemptRollback: rollbackBytes =>
                    {
                        inFlightForSeg = 0;
                        if (rollbackBytes > 0)
                        {
                            Interlocked.Add(ref downloadedBytes, -rollbackBytes);
                            workerBytes = Math.Max(0, workerBytes - rollbackBytes);
                            row.ActualBytes = workerBytes;
                        }
                    }).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(seg.KeyUrl))
                {
                    var key = await GetKeyAsync(seg.KeyUrl!).ConfigureAwait(false);
                    data = DecryptAes128(data, key, seg.ExplicitIv, seg.Index);
                }

                var partPath = Path.Combine(segDir, $"{seg.Index:D6}.part");
                await File.WriteAllBytesAsync(partPath, data, ct).ConfigureAwait(false);

                Interlocked.Add(ref completedSegmentBytes, data.Length);
                Interlocked.Increment(ref completedCount);

                assignedDone++;
                row.Downloaded = Math.Min(row.Length, assignedDone * 1000L);
            }
            row.Downloaded = row.Length;
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            tickerCts.Cancel();
            await tickerTask.ConfigureAwait(false);
        }
        ReportProgress();

        // Concatenate all segments in order into outputStreamPath
        await using (var outStream = new FileStream(outputStreamPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
        {
            if (initSegmentBytes is { Length: > 0 })
                await outStream.WriteAsync(initSegmentBytes, ct).ConfigureAwait(false);

            var buffer = new byte[131072];
            for (var i = 0; i < segments.Count; i++)
            {
                var partPath = Path.Combine(segDir, $"{segments[i].Index:D6}.part");
                await using (var partStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
                {
                    int read;
                    while ((read = await partStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                try { File.Delete(partPath); } catch { }
            }
        }

        try { Directory.Delete(segDir, recursive: true); } catch { }
    }

    private static (string? InitMapUrl, string? InitKeyUrl, byte[]? InitIv, List<HlsSegment> Segments) ParseMediaSegments(string text, Uri baseUri)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var segments = new List<HlsSegment>();
        string? currentKeyUrl = null;
        byte[]? currentIv = null;
        string? initMapUrl = null;
        string? initKeyUrl = null;
        byte[]? initIv = null;
        var mediaSequence = 0;
        var nextDuration = 0.0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out mediaSequence);
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                var method = attrs.GetValueOrDefault("METHOD") ?? "NONE";
                if (string.Equals(method, "AES-128", StringComparison.OrdinalIgnoreCase) && attrs.TryGetValue("URI", out var kuri))
                {
                    currentKeyUrl = new Uri(baseUri, kuri).AbsoluteUri;
                    currentIv = attrs.TryGetValue("IV", out var ivHex) ? ParseHexIv(ivHex) : null;
                }
                else if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    currentKeyUrl = null;
                    currentIv = null;
                }
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                if (attrs.TryGetValue("URI", out var muri) && !string.IsNullOrWhiteSpace(muri))
                {
                    initMapUrl = new Uri(baseUri, muri).AbsoluteUri;
                    initKeyUrl = currentKeyUrl;
                    initIv = currentIv;
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var durPart = line["#EXTINF:".Length..].Split(',')[0].Trim();
                double.TryParse(durPart, NumberStyles.Float, CultureInfo.InvariantCulture, out nextDuration);
            }
            else if (!line.StartsWith('#'))
            {
                var segUrl = new Uri(baseUri, line).AbsoluteUri;
                var seqIndex = mediaSequence + segments.Count;
                segments.Add(new HlsSegment(seqIndex, segUrl, nextDuration, currentKeyUrl, currentIv));
                nextDuration = 0;
            }
        }

        return (initMapUrl, initKeyUrl, initIv, segments);
    }

    private static byte[] DecryptAes128(byte[] cipherText, byte[] key, byte[]? explicitIv, int mediaSequence)
    {
        if (key.Length != 16) return cipherText;
        var iv = explicitIv;
        if (iv is null || iv.Length != 16)
        {
            iv = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8, 8), (ulong)mediaSequence);
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
    }

    private static byte[]? ParseHexIv(string hex)
    {
        var clean = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (clean.Length != 32) return null;
        try { return Convert.FromHexString(clean); } catch { return null; }
    }

    private static string BuildVariantLabel(int? height, long? bandwidth, string? name)
    {
        string head;
        if (height is > 0)
        {
            var badge = height.Value switch
            {
                >= 2160 => " 4K",
                >= 1440 => " 2K",
                1080 => " Full HD",
                720 => " HD",
                _ => "",
            };
            head = $"{height.Value}p{badge}";
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            head = name!;
        }
        else
        {
            head = "Stream";
        }

        if (bandwidth is > 0)
        {
            var mbps = bandwidth.Value / 1_000_000.0;
            return $"{head} (MP4 · {mbps:F1} Mbps)";
        }
        return $"{head} (MP4)";
    }

    private static double EstimateTotalDuration(string text)
    {
        var sum = 0.0;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) continue;
            var durPart = line["#EXTINF:".Length..].Split(',')[0].Trim();
            if (double.TryParse(durPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                sum += d;
        }
        return sum;
    }

    private static Dictionary<string, string> ParseAttributes(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex.Matches(raw))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            dict[key] = val;
        }
        return dict;
    }

    private static HttpRequestMessage BuildRequest(string url, string? referer, string? origin, string? userAgent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd(userAgent);
        }
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
        {
            req.Headers.Referrer = refUri;
        }
        if (!string.IsNullOrWhiteSpace(origin))
        {
            req.Headers.TryAddWithoutValidation("Origin", origin);
        }
        return req;
    }

    private static async Task<(string Text, Uri FinalUri)> FetchTextAsync(
        HttpClient http, string url, string? referer, string? origin, string? userAgent, CancellationToken ct)
    {
        using var req = BuildRequest(url, referer, origin, userAgent);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var finalUri = resp.RequestMessage?.RequestUri ?? new Uri(url);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (Encoding.UTF8.GetString(bytes), finalUri);
    }

    private static async Task<byte[]> FetchBytesAsync(
        HttpClient http, string url, string? referer, string? origin, string? userAgent, CancellationToken ct)
    {
        using var req = BuildRequest(url, referer, origin, userAgent);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> FetchBytesStreamingAsync(
        HttpClient http, string url, string? referer, string? origin, string? userAgent, CancellationToken ct,
        Action<int>? onChunkRead, Action<long>? onAttemptRollback)
    {
        using var req = BuildRequest(url, referer, origin, userAgent);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        if (onChunkRead is null)
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var capacity = resp.Content.Headers.ContentLength is > 0 and < 64 * 1024 * 1024
            ? (int)resp.Content.Headers.ContentLength.Value
            : 65536;
        using var ms = new MemoryStream(capacity);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768);
        long attemptBytes = 0;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                attemptBytes += read;
                onChunkRead(read);
            }
            return ms.ToArray();
        }
        catch
        {
            if (attemptBytes > 0) onAttemptRollback?.Invoke(attemptBytes);
            throw;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> FetchWithRetryAsync(
        HttpClient http, string url, string? referer, string? origin, string? userAgent, CancellationToken ct,
        Action<int>? onChunkRead = null, Action<long>? onAttemptRollback = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await FetchBytesStreamingAsync(http, url, referer, origin, userAgent, ct, onChunkRead, onAttemptRollback).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(350 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw last!;
    }

    private static async Task<bool> TryRunFfmpegAsync(string ffmpegPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return false;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode == 0;
    }
}
