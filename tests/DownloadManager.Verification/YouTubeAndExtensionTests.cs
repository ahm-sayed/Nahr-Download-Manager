using System.Net;
using System.Net.Http;
using System.Text;
using DownloadManager.Core.Models;
using DownloadManager.Engine;
using DownloadManager.Engine.Hls;
using DownloadManager.Engine.YouTube;
using DownloadManager.Presentation.Mvvm;
using DownloadManager.Presentation.ViewModels;

namespace DownloadManager.Verification;

internal static class YouTubeAndExtensionTests
{
    public static IEnumerable<TestCase> All() =>
    [
        new("youtube: IsYouTubeUrl detects standard video URLs", _ =>
        {
            Assert.True(YouTubeService.IsYouTubeUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "standard watch URL");
            Assert.True(YouTubeService.IsYouTubeUrl("https://youtube.com/watch?v=dQw4w9WgXcQ"), "no www");
            Assert.True(YouTubeService.IsYouTubeUrl("https://youtu.be/dQw4w9WgXcQ"), "short URL");
            Assert.True(YouTubeService.IsYouTubeUrl("https://www.youtube.com/shorts/dQw4w9WgXcQ"), "shorts");
            Assert.True(YouTubeService.IsYouTubeUrl("https://www.youtube.com/embed/dQw4w9WgXcQ"), "embed");
            Assert.True(YouTubeService.IsYouTubeUrl("https://m.youtube.com/watch?v=dQw4w9WgXcQ"), "mobile");
            Assert.True(YouTubeService.IsYouTubeUrl("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"), "playlist");
            return Task.CompletedTask;
        }),

        new("youtube: IsYouTubeUrl rejects non-YouTube URLs", _ =>
        {
            Assert.True(!YouTubeService.IsYouTubeUrl("https://www.google.com"), "google");
            Assert.True(!YouTubeService.IsYouTubeUrl("https://vimeo.com/123456"), "vimeo");
            Assert.True(!YouTubeService.IsYouTubeUrl("https://example.com/youtube.com"), "fake");
            Assert.True(!YouTubeService.IsYouTubeUrl(null), "null");
            Assert.True(!YouTubeService.IsYouTubeUrl(""), "empty");
            Assert.True(!YouTubeService.IsYouTubeUrl("not a url"), "plain text");
            return Task.CompletedTask;
        }),

        new("youtube: IsPlaylistUrl detects playlist URLs", _ =>
        {
            Assert.True(YouTubeService.IsPlaylistUrl("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"), "playlist page");
            Assert.True(YouTubeService.IsPlaylistUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"), "video in playlist");
            Assert.True(!YouTubeService.IsPlaylistUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "single video has no list");
            Assert.True(!YouTubeService.IsPlaylistUrl("https://www.google.com"), "not youtube");
            return Task.CompletedTask;
        }),

        new("youtube: ExtractVideoId extracts 11-char IDs", _ =>
        {
            Assert.Equal("dQw4w9WgXcQ", YouTubeService.ExtractVideoId("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "standard");
            Assert.Equal("dQw4w9WgXcQ", YouTubeService.ExtractVideoId("https://youtu.be/dQw4w9WgXcQ"), "short");
            Assert.Equal("dQw4w9WgXcQ", YouTubeService.ExtractVideoId("https://www.youtube.com/shorts/dQw4w9WgXcQ"), "shorts");
            Assert.Equal("dQw4w9WgXcQ", YouTubeService.ExtractVideoId("https://www.youtube.com/embed/dQw4w9WgXcQ"), "embed");
            Assert.True(YouTubeService.ExtractVideoId("https://www.google.com") is null, "non-youtube returns null");
            return Task.CompletedTask;
        }),

        new("youtube: ExtractPlaylistId extracts playlist IDs", _ =>
        {
            Assert.Equal("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf",
                YouTubeService.ExtractPlaylistId("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"), "playlist page");
            Assert.Equal("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf",
                YouTubeService.ExtractPlaylistId("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"), "video in playlist");
            Assert.True(YouTubeService.ExtractPlaylistId("https://www.youtube.com/watch?v=dQw4w9WgXcQ") is null, "no list param");
            return Task.CompletedTask;
        }),

        new("youtube: YouTubeService reports availability correctly", _ =>
        {
            var svc = new YouTubeService();
            if (svc.IsAvailable)
                Assert.True(File.Exists(svc.YtDlpPath!), "path should point to an existing file");
            if (svc.HasFfmpeg)
                Assert.True(File.Exists(svc.FfmpegPath!), "ffmpeg path should point to an existing file");
            return Task.CompletedTask;
        }),

        new("youtube: PickFormat selects requested resolution or audio-only and caches direct URL", async _ =>
        {
            var f1080 = new YouTubeFormat("137", "1080p", "mp4", 50_000_000, 1080, 1920, "avc1", null, true, false, false,
                DirectUrl: "https://cdn.example/v1080.mp4", AudioDirectUrl: "https://cdn.example/a140.m4a", AudioSizeApprox: 3_000_000);
            var f720 = new YouTubeFormat("136", "720p", "mp4", 25_000_000, 720, 1280, "avc1", null, true, false, false,
                DirectUrl: "https://cdn.example/v720.mp4", AudioDirectUrl: "https://cdn.example/a140.m4a", AudioSizeApprox: 3_000_000);
            var f360 = new YouTubeFormat("18", "360p", "mp4", 10_000_000, 360, 640, "avc1", "mp4a", true, true, true,
                DirectUrl: "https://cdn.example/v360.mp4");
            var fAudio = new YouTubeFormat("140", "audio", "m4a", 3_000_000, null, null, null, "mp4a", false, true, false,
                DirectUrl: "https://cdn.example/a140.m4a");

            var info = new YouTubeVideoInfo
            {
                Id = "abc12345678",
                Title = "Sample Video",
                Formats = [f1080, f720, f360, fAudio],
                QualityOptions = [f1080, f720, f360, fAudio],
            };

            Assert.Equal("137", YouTubeService.PickFormat(info, "best")!.FormatId, "best picks 1080p");
            Assert.Equal("136", YouTubeService.PickFormat(info, "720")!.FormatId, "720 picks 720p");
            Assert.Equal("136", YouTubeService.PickFormat(info, "900")!.FormatId, "clamps to <= requested height");
            Assert.Equal("18", YouTubeService.PickFormat(info, "360")!.FormatId, "360 picks 360p");
            Assert.Equal("140", YouTubeService.PickFormat(info, "audio")!.FormatId, "audio picks m4a");
            Assert.True(f1080.NeedsAudioMerge && !f360.NeedsAudioMerge && !fAudio.NeedsAudioMerge, "NeedsAudioMerge flags");

            var svc = new YouTubeService();
            var direct = await svc.GetDirectUrlAsync("https://www.youtube.com/watch?v=abc12345678", f720);
            Assert.Equal("https://cdn.example/v720.mp4", direct, "uses cached DirectUrl in 0ms without spawning yt-dlp");
        }),

        new("standalone window: waits for manual Start, applies quality & global connections, transitions to live progress", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env, tweakSettings: s => s.DefaultConnections = 6);

            DownloadSessionViewModel? openedSession = null;
            await rig.Ui.Run(() =>
            {
                rig.Vm.DownloadWindowRequested += (_, s) => openedSession = s;
            });

            var urlLow = env.Server.Url("range", 400_000, "video_360.mp4").AbsoluteUri;
            var urlHigh = env.Server.Url("range", 1_200_000, "video_1080.mp4").AbsoluteUri;

            await rig.Ui.Run(() =>
            {
                rig.Vm.OpenStandaloneDownloadPrompt(new ExtensionDownloadRequest
                {
                    Url = urlLow,
                    PageTitle = "Lecture Clip",
                    MediaType = "video",
                    Qualities =
                    [
                        new ExtensionMediaOption { Label = "360p (MP4)", Url = urlLow, Extension = "mp4", FileSize = 400_000 },
                        new ExtensionMediaOption { Label = "1080p Full HD (MP4)", Url = urlHigh, Extension = "mp4", FileSize = 1_200_000 },
                    ]
                });
            });

            Assert.NotNull(openedSession, "standalone DownloadSessionViewModel opened");
            await rig.Ui.Run(() =>
            {
                Assert.True(openedSession!.IsPreDownload && !openedSession.IsProgressMode, "starts in Pre-Download confirmation mode");
                Assert.Equal(0, rig.Vm.Downloads.Count, "nothing starts downloading before user clicks Start");
                Assert.Equal(2, openedSession.Qualities.Count, "quality choices populated");

                // User picks 1080p quality
                openedSession.SelectedQuality = openedSession.Qualities[1];
                Assert.Equal(urlHigh, openedSession.Url, "selecting quality updates target stream URL");
            });

            // Now user clicks "Start Download"
            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)openedSession!.StartDownloadCommand).ExecuteAsync());

            await rig.Ui.Run(() =>
            {
                Assert.True(!openedSession!.IsPreDownload && openedSession.IsProgressMode, "window transitioned into Live Progress mode");
                Assert.NotNull(openedSession.Item, "bound to active DownloadItemViewModel");
                Assert.Equal(6, openedSession.Item!.Connections, "uses global DefaultConnections (6) from Settings");
            });

            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "download completed");
            await rig.Ui.Run(() => Assert.True(openedSession!.IsCompleted, "standalone window reflects completion"));
        }),

        new("video & audio mode switcher: separate qualities for Video vs Audio, extension & category sync, and PreferAudio", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            DownloadSessionViewModel? session = null;
            await rig.Ui.Run(() => rig.Vm.DownloadWindowRequested += (_, s) => session = s);

            var url720 = env.Server.Url("range", 700_000, "clip_720.mp4").AbsoluteUri;
            var url1080 = env.Server.Url("range", 1_500_000, "clip_1080.mp4").AbsoluteUri;

            await rig.Ui.Run(() =>
            {
                rig.Vm.OpenStandaloneDownloadPrompt(new ExtensionDownloadRequest
                {
                    Url = url1080,
                    PageTitle = "My Favorite Song Clip",
                    MediaType = "video",
                    Qualities =
                    [
                        new ExtensionMediaOption { Label = "1080p Full HD (MP4)", Url = url1080, Extension = "mp4", FileSize = 1_500_000 },
                        new ExtensionMediaOption { Label = "720p HD (MP4)", Url = url720, Extension = "mp4", FileSize = 700_000 },
                    ]
                });
            });

            Assert.NotNull(session, "session created");
            await rig.Ui.Run(() =>
            {
                Assert.True(session!.IsMediaClip, "detected as media clip");
                Assert.True(session.IsVideoMode && !session.IsAudioMode, "starts in Video mode by default");
                Assert.Equal(2, session.Qualities.Count, "Video mode has 2 video qualities");
                Assert.True(session.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase), $"video filename ends with .mp4: {session.FileName}");
                Assert.Equal(rig.Vm.Loc["cat_Video"], session.CategoryText, "category is Video");

                // Select second video quality (720p) to verify selection memory
                session.SelectedQuality = session.Qualities[1];
                Assert.True(session.SelectedQuality!.Label.Contains("720p"), "720p selected");

                // Switch to Audio mode -> swaps to dedicated audio qualities (MP3 320/192/128, M4A High/Low)
                session.IsAudioMode = true;
                Assert.True(session.IsAudioMode && !session.IsVideoMode, "switched to Audio mode");
                Assert.Equal(5, session.Qualities.Count, "Audio mode has 5 dedicated audio quality options");
                Assert.Equal("mp3:320", session.SelectedQuality!.Key, "defaults to MP3 320 kbps");
                Assert.True(session.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase), $"audio filename switched to .mp3: {session.FileName}");
                Assert.Equal(rig.Vm.Loc["cat_Music"], session.CategoryText, "category switched to Music");

                // Switch to M4A audio quality -> updates extension to .m4a
                var m4aChoice = session.Qualities.First(q => q.Extension == "m4a");
                session.SelectedQuality = m4aChoice;
                Assert.True(session.FileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase), $"audio filename switched to .m4a: {session.FileName}");

                // Switch back to Video mode -> restores remembered 720p video quality & .mp4 extension!
                session.IsVideoMode = true;
                Assert.True(session.IsVideoMode && !session.IsAudioMode, "switched back to Video mode");
                Assert.Equal(2, session.Qualities.Count, "restored 2 video qualities");
                Assert.True(session.SelectedQuality!.Label.Contains("720p"), "remembered 720p video choice");
                Assert.True(session.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase), $"restored .mp4 filename: {session.FileName}");
                Assert.Equal(rig.Vm.Loc["cat_Video"], session.CategoryText, "restored Video category");

                // Also test probed YouTube formats populating both Video & Audio quality lists with bitrates/sizes
                var f2160 = new YouTubeFormat("313", "2160p", "mp4", 120_000_000, 2160, 3840, "vp9", null, true, false, false, DirectUrl: url1080);
                var f1080 = new YouTubeFormat("137", "1080p", "mp4", 40_000_000, 1080, 1920, "avc1", null, true, false, false, DirectUrl: url1080);
                var fAudioBest = new YouTubeFormat("140", "128 kbps", "m4a", 3_200_000, null, null, null, "mp4a", false, true, false, DirectUrl: url720, AudioBitrateKbps: 128);
                session.ApplyYouTubeVideoProbe(new YouTubeVideoInfo
                {
                    Id = "vid12345678",
                    Title = "Dr. Strange — Official Clip",
                    Duration = TimeSpan.FromMinutes(3),
                    Formats = [f2160, f1080, fAudioBest],
                    QualityOptions = [f2160, f1080],
                    AudioOptions = [fAudioBest],
                });

                Assert.Equal(2, session.Qualities.Count, "probed 2160p and 1080p video options");
                session.IsAudioMode = true;
                Assert.Equal(5, session.Qualities.Count, "probed 5 audio options with estimated MB sizes");
                Assert.True(session.Qualities[0].Label.Contains("MB"), $"audio quality includes estimated size: {session.Qualities[0].Label}");
                Assert.Equal("Dr. Strange — Official Clip.m4a", session.FileName, "remembered M4A audio preference across probe while preserving dots in title");

                session.SelectedQuality = session.Qualities[0]; // MP3 320 kbps
                Assert.Equal("Dr. Strange — Official Clip.mp3", session.FileName, "preserves dots in title while applying .mp3");
            });

            // Verify PreferAudio = true from browser bar [♪ Audio] button opens directly in Audio mode
            DownloadSessionViewModel? audioSession = null;
            await rig.Ui.Run(() =>
            {
                rig.Vm.DownloadWindowRequested += (_, s) => audioSession = s;
                rig.Vm.OpenStandaloneDownloadPrompt(new ExtensionDownloadRequest
                {
                    Url = url720,
                    PageTitle = "Podcast Episode",
                    MediaType = "video",
                    PreferAudio = true,
                });
            });

            Assert.NotNull(audioSession, "PreferAudio session created");
            await rig.Ui.Run(() =>
            {
                Assert.True(audioSession!.IsAudioMode && !audioSession.IsVideoMode, "PreferAudio opens directly in Audio mode");
                Assert.Equal(5, audioSession.Qualities.Count, "Audio qualities shown immediately");
                Assert.True(audioSession.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase), $"starts with .mp3 filename: {audioSession.FileName}");
            });

            // Also verify AddDownloadViewModel Video/Audio switcher
            await rig.Ui.Run(() =>
            {
                rig.Vm.OpenAddDialog("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
                Assert.NotNull(rig.Vm.AddDialog, "AddDialog opened");
                Assert.True(rig.Vm.AddDialog!.HasYouTubeUrl, "detected YouTube URL");
                Assert.True(rig.Vm.AddDialog.IsVideoMode, "AddDialog starts in Video mode");
                Assert.Equal("best", rig.Vm.AddDialog.SelectedQuality?.Key, "Video preset selected");

                rig.Vm.AddDialog.IsAudioMode = true;
                Assert.True(rig.Vm.AddDialog.IsAudioMode, "AddDialog switched to Audio mode");
                Assert.Equal("mp3:320", rig.Vm.AddDialog.SelectedQuality?.Key, "Audio MP3 320 kbps preset selected");
                rig.Vm.AddDialog.CancelCommand.Execute(null);
            });
        }),

        new("hls & floating tracker: probes master .m3u8 qualities, activates screen tracker on Start, downloads parallel segments", async env =>
        {
            var hlsPort = 40000 + Random.Shared.Next(10000);
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{hlsPort}/");
            listener.Start();

            var seg1 = Encoding.UTF8.GetBytes("SEGMENT_ONE_DATA_PAYLOAD_1234567890");
            var seg2 = Encoding.UTF8.GetBytes("SEGMENT_TWO_DATA_PAYLOAD_0987654321");

            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch { break; }

                    var path = ctx.Request.Url?.AbsolutePath ?? "";
                    byte[] payload;
                    string contentType = "application/vnd.apple.mpegurl";

                    if (path == "/master.txt") // Disguised extension just like streaming CDNs!
                    {
                        payload = Encoding.UTF8.GetBytes($"""
                            #EXTM3U
                            #EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1920x1080
                            /v1080.m3u8
                            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720
                            /v720.m3u8
                            """);
                    }
                    else if (path is "/v1080.m3u8" or "/v720.m3u8")
                    {
                        payload = Encoding.UTF8.GetBytes("""
                            #EXTM3U
                            #EXT-X-TARGETDURATION:4
                            #EXTINF:4.0,
                            /seg1.jpg
                            #EXTINF:4.0,
                            /seg2.jpg
                            #EXT-X-ENDLIST
                            """);
                    }
                    else if (path == "/seg1.jpg")
                    {
                        contentType = "application/octet-stream";
                        payload = seg1;
                    }
                    else
                    {
                        contentType = "application/octet-stream";
                        payload = seg2;
                    }

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = contentType;
                    ctx.Response.ContentLength64 = payload.Length;
                    await ctx.Response.OutputStream.WriteAsync(payload);
                    ctx.Response.Close();
                }
            });

            try
            {
                await using var rig = await VmRig.CreateAsync(env);
                DownloadSessionViewModel? session = null;
                var sawFloatingTracker = false;

                await rig.Ui.Run(() =>
                {
                    rig.Vm.DownloadWindowRequested += (_, s) => session = s;
                    rig.Vm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(MainViewModel.HasFloatingTracker) && rig.Vm.HasFloatingTracker)
                            sawFloatingTracker = true;
                    };

                    rig.Vm.OpenStandaloneDownloadPrompt(new ExtensionDownloadRequest
                    {
                        Url = $"http://127.0.0.1:{hlsPort}/master.txt",
                        PageTitle = "Cinema Movie",
                        MediaType = "hls",
                        Referrer = "https://cinejoy.example/watch",
                        Origin = "https://cinejoy.example",
                    });
                });

                Assert.NotNull(session, "HLS session created");
                if (session!.BackgroundProbeTask is not null) await session.BackgroundProbeTask;

                await rig.Ui.Run(() =>
                {
                    Assert.Equal(2, session.Qualities.Count, "parsed both 1080p and 720p HLS variants from disguised master.txt");
                    Assert.True(session.Qualities[0].Label.Contains("1080p"), $"first variant is 1080p: {session.Qualities[0].Label}");
                    Assert.True(session.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase), $"output file name is .mp4: {session.FileName}");
                });

                // Start HLS download (disable ffmpeg remux for synthetic non-MPEGTS test bytes by clearing FfmpegPath via engine test)
                await rig.Ui.RunAsync(() => ((AsyncRelayCommand)session.StartDownloadCommand).ExecuteAsync());
                await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "HLS stream download completed");

                Assert.True(sawFloatingTracker, "On-screen FloatingTracker activated during download");
                var savedPath = await rig.Ui.Run(() => rig.Vm.Downloads[0].FilePath);
                Assert.True(File.Exists(savedPath) && new FileInfo(savedPath).Length == seg1.Length + seg2.Length, "all HLS segments concatenated accurately");
            }
            finally
            {
                listener.Stop();
            }
        }),

        new("cinejoy / lightspeed.st HLS: extracts resolution from URL, discovers sibling qualities (1080p/720p/360p), and honors pre-selected browser quality", async env =>
        {
            var hlsPort = 40000 + Random.Shared.Next(10000);
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{hlsPort}/");
            listener.Start();

            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch { break; }

                    var path = ctx.Request.Url?.AbsolutePath ?? "";
                    if (path is "/hls/32e0cce2/2/1080p/playlist.m3u8"
                             or "/hls/32e0cce2/2/720p/playlist.m3u8"
                             or "/hls/32e0cce2/2/360p/playlist.m3u8")
                    {
                        var payload = Encoding.UTF8.GetBytes("""
                            #EXTM3U
                            #EXT-X-TARGETDURATION:10
                            #EXTINF:120.0,
                            /seg1.ts
                            #EXT-X-ENDLIST
                            """);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                        ctx.Response.ContentLength64 = payload.Length;
                        await ctx.Response.OutputStream.WriteAsync(payload);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    ctx.Response.Close();
                }
            });

            try
            {
                Assert.Equal(1080, HlsStreamService.TryExtractHeightFromUrl(
                    "https://lightspeed.st/hls/32e0cce2-76e0-4aae-b641-5efc26f14ee5/2/1080p/playlist.m3u8"),
                    "extracts 1080 from lightspeed.st URL path");

                await using var rig = await VmRig.CreateAsync(env);
                DownloadSessionViewModel? session = null;
                await rig.Ui.Run(() =>
                {
                    rig.Vm.DownloadWindowRequested += (_, s) => session = s;
                    rig.Vm.OpenStandaloneDownloadPrompt(new ExtensionDownloadRequest
                    {
                        Url = $"http://127.0.0.1:{hlsPort}/hls/32e0cce2/2/1080p/playlist.m3u8",
                        PreferredQualityKey = "720", // User picked 720p in the in-browser IDM menu before clicking download!
                        PageTitle = "The Odyssey",
                        MediaType = "hls",
                    });
                });

                Assert.NotNull(session, "session opened");
                if (session!.BackgroundProbeTask is not null) await session.BackgroundProbeTask;

                await rig.Ui.Run(() =>
                {
                    Assert.Equal(3, session.Qualities.Count, "discovered 1080p, 720p, and 360p sibling qualities automatically");
                    Assert.True(session.Qualities[0].Label.Contains("1080p Full HD"), $"first is 1080p Full HD: {session.Qualities[0].Label}");
                    Assert.True(session.Qualities[1].Label.Contains("720p HD"), $"second is 720p HD: {session.Qualities[1].Label}");
                    Assert.True(session.Qualities[2].Label.Contains("360p"), $"third is 360p: {session.Qualities[2].Label}");
                    Assert.True(session.SelectedQuality!.Label.Contains("720p HD"), $"pre-selected 720p HD from browser menu: {session.SelectedQuality.Label}");
                    Assert.True(session.Url.Contains("/720p/playlist.m3u8"), $"target URL updated to 720p stream: {session.Url}");
                    Assert.True(session.SizeText.Contains("720p HD"), $"badge clearly shows selected resolution: {session.SizeText}");
                });
            }
            finally
            {
                listener.Stop();
            }
        }),

        new("compact list: playlist videos are nested under one playlist row; multi-select bulk remove & delete", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            var dir = env.Dir;

            // 3 videos of one playlist + 2 normal files
            var urls = new[] { "ep1.mp4", "ep2.mp4", "ep3.mp4" }
                .Select(n => env.Server.Url("range", 120_000, n).AbsoluteUri).ToArray();
            foreach (var u in urls)
            {
                var item = await rig.Engine.AddAsync(new AddDownloadRequest(u, dir) { GroupId = "pl:TEST1", GroupTitle = "My Course" }, env.Ct);
                await rig.Engine.StartAsync(item.Id, env.Ct);
            }
            foreach (var n in new[] { "solo1.zip", "solo2.pdf" })
            {
                var item = await rig.Engine.AddAsync(new AddDownloadRequest(env.Server.Url("range", 90_000, n).AbsoluteUri, dir), env.Ct);
                await rig.Engine.StartAsync(item.Id, env.Ct);
            }

            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 5 && vm.Downloads.All(d => d.Status == DownloadStatus.Completed), "5 completed");

            await rig.Ui.Run(() =>
            {
                var vm = rig.Vm;
                Assert.Equal(3, vm.VisibleDownloads.Count, "1 playlist row + 2 single rows (not 5 loose rows)");
                var pl = vm.VisibleDownloads.Single(r => r.IsPlaylistGroup);
                Assert.Equal("My Course", pl.FileName, "playlist row titled with playlist name");
                Assert.Equal(3, pl.Children.Count, "3 videos nested inside the playlist");
                Assert.Equal("ep1.mp4", pl.Children[0].FileName, "episodes listed in natural order");
                Assert.Equal(DownloadStatus.Completed, pl.Status, "aggregate status");
                Assert.Equal(100.0, pl.ProgressPercent, "aggregate progress");
                Assert.True(!pl.IsExpanded, "collapsed by default to keep the list short");

                // Searching inside a playlist finds the episode and expands the group
                vm.SearchText = "ep2";
                Assert.True(vm.VisibleDownloads.Count == 1 && vm.VisibleDownloads[0].IsPlaylistGroup, "only playlist row remains");
                Assert.True(vm.VisibleDownloads[0].Children.Count == 1 && vm.VisibleDownloads[0].IsExpanded, "matching episode shown, group expanded");
                vm.SearchText = "";

                // Multi-select: checking the playlist checks all its videos
                pl.IsChecked = true;
                Assert.True(pl.Children.All(c => c.IsChecked), "playlist checkbox cascades to children");
                Assert.Equal(3, vm.CheckedCount, "3 checked");
                Assert.True(vm.HasCheckedItems && !vm.ShowStandardToolbar, "bulk action bar visible");

                // Unchecking one child un-checks the playlist header
                pl.Children[0].IsChecked = false;
                Assert.True(!pl.IsChecked && vm.CheckedCount == 2, "partial selection");

                vm.ClearSelection();
                Assert.Equal(0, vm.CheckedCount, "cleared");

                // Select-all then deselect one single file
                vm.IsAllChecked = true;
                Assert.Equal(5, vm.CheckedCount, "select-all checks every visible file (incl. nested)");
                vm.VisibleDownloads.First(r => r.IsSingleItem).IsChecked = false;
                Assert.True(!vm.IsAllChecked && vm.CheckedCount == 4, "select-all reflects partial state");
            });

            // Bulk remove from list (files stay on disk): the 2 videos still checked + 2nd... => 4 checked
            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.RemoveCheckedCommand).ExecuteAsync());
            await rig.Ui.Run(() =>
            {
                Assert.Equal(1, rig.Vm.Downloads.Count, "4 removed from list");
                Assert.Equal(0, rig.Vm.CheckedCount, "selection cleared after removal");
                Assert.Equal(5, Directory.GetFiles(dir).Length, "files kept on disk when removing from list");
                rig.Vm.IsAllChecked = true;
            });

            // Bulk delete files from disk (with one confirmation)
            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.DeleteCheckedFilesCommand).ExecuteAsync());
            await rig.Ui.Run(() =>
            {
                Assert.Equal(0, rig.Vm.Downloads.Count, "list empty");
                Assert.Equal(4, Directory.GetFiles(dir).Length, "the selected file was deleted from disk");
            });
        }),

        new("idm progress window: tabs, hide/show details, per-connection table and per-download speed limiter", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            await using var rig = await VmRig.CreateAsync(env);
            DownloadSessionViewModel? session = null;
            await rig.Ui.Run(() => rig.Vm.DownloadWindowRequested += (_, s) => session = s);

            var item = await rig.Engine.AddAsync(new AddDownloadRequest(env.Server.Url("range+slow", 4 * 1024 * 1024, "movie.mp4").AbsoluteUri, env.Dir) { Connections = 4 }, env.Ct);
            await rig.Engine.StartAsync(item.Id, env.Ct);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].ProgressPercent > 5, "progress");

            await rig.Ui.Run(() =>
            {
                rig.Vm.Downloads[0].OpenProgressWindowCommand.Execute(null);
                Assert.NotNull(session, "progress window opened");
                var s = session!;
                Assert.True(s.IsProgressMode && s.IsStatusTab && !s.IsSpeedLimiterTab, "opens on Download status tab");
                Assert.True(s.ShowDetails && s.ShowConnectionDetails, "connection details shown by default");
                Assert.Equal("<< Hide details", s.DetailsToggleText, "hide details label");
                Assert.Equal(4, s.Item!.Segments.Count, "4 connection rows");
                Assert.True(s.Item.Segments.All(x => x.InfoText.Length > 0 && x.BytesOnlyText.EndsWith("B")), "N./Downloaded/Info columns populated");
                Assert.True(s.Item.DetailedDownloadedText.Contains("%"), $"downloaded with exact %: {s.Item.DetailedDownloadedText}");

                s.ToggleDetailsCommand.Execute(null);
                Assert.True(!s.ShowConnectionDetails && s.DetailsToggleText == "Show details >>", "details collapsed");

                s.IsSpeedLimiterTab = true;
                Assert.True(s.IsSpeedLimiterTab && !s.IsStatusTab, "switched tab");
                s.SpeedLimitKBpsText = "300";
                s.UseSpeedLimiter = true;
                Assert.Equal(300L * 1024, rig.Engine.Get(item.Id)!.SpeedLimitBytesPerSecond, "speed limit applied to this download in the engine");
                s.UseSpeedLimiter = false;
                Assert.Equal(0L, rig.Engine.Get(item.Id)!.SpeedLimitBytesPerSecond, "limit removed");
            });

            await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Completed, "completed");
        }),

        new("progress performance: progress ticks do not re-trigger layout properties and update dynamic HLS size", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            var item = new DownloadItem
            {
                Url = "https://example.test/stream.m3u8",
                FileName = "stream.mp4",
                FilePath = Path.Combine(env.Dir, "stream.mp4"),
                Status = DownloadStatus.Downloading,
                TotalBytes = 10 * 1024 * 1024,
            };

            await rig.Ui.Run(() =>
            {
                var vm = new DownloadItemViewModel(item, rig.Vm.Loc, rig.Vm);
                var session = new DownloadSessionViewModel(vm, rig.Vm.Loc, _ => null);
                var layoutPropHits = 0;
                session.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(DownloadSessionViewModel.ShowConnectionDetails)
                        or nameof(DownloadSessionViewModel.ShowDetails)
                        or nameof(DownloadSessionViewModel.IsStatusTab))
                    {
                        layoutPropHits++;
                    }
                };

                for (var i = 1; i <= 20; i++)
                {
                    vm.ApplyProgress(new DownloadProgress(
                        item.Id, DownloadStatus.Downloading,
                        i * 512L * 1024,
                        (10 + i) * 1024L * 1024, // dynamic HLS total refinement
                        2_500_000, 2_500_000,
                        TimeSpan.FromSeconds(30 - i), 8));
                }

                Assert.Equal(0, layoutPropHits, "progress ticks must never spam ShowConnectionDetails layout invalidation");
                Assert.True(vm.SizeText.Contains("30"), $"dynamic TotalBytes should update live (got {vm.SizeText})");
            });
        }),

        new("youtube native extractor: parses InnerTube playlist (lockupViewModel & playlistVideoRenderer) and player streams without yt-dlp", _ =>
        {
            var browseJson = """
            {
              "metadata": { "playlistMetadataRenderer": { "title": "Tajweed Series" } },
              "contents": {
                "items": [
                  {
                    "lockupViewModel": {
                      "contentId": "abc12345678",
                      "metadata": { "lockupMetadataViewModel": { "title": { "content": "Lesson 1: Intro" } } },
                      "contentImage": {
                        "thumbnailViewModel": {
                          "overlays": [
                            { "thumbnailBottomOverlayViewModel": { "badges": [{ "thumbnailBadgeViewModel": { "text": "12:34" } }] } }
                          ]
                        }
                      }
                    }
                  },
                  {
                    "playlistVideoRenderer": {
                      "videoId": "def87654321",
                      "title": { "runs": [{ "text": "Lesson 2: Rules" }] },
                      "lengthSeconds": "540"
                    }
                  }
                ]
              }
            }
            """;

            var parsedPl = YouTubeService.ParseNativePlaylistBrowseJson(browseJson, "PLtest123");
            Assert.Equal("Tajweed Series", parsedPl.Title, "playlist title");
            Assert.Equal(2, parsedPl.Entries.Count, "parsed both lockupViewModel and playlistVideoRenderer");
            Assert.Equal("abc12345678", parsedPl.Entries[0].VideoId, "first video id");
            Assert.Equal("Lesson 1: Intro", parsedPl.Entries[0].Title, "first video title");
            Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(34), parsedPl.Entries[0].Duration, "first duration");
            Assert.Equal("def87654321", parsedPl.Entries[1].VideoId, "second video id");
            Assert.Equal(TimeSpan.FromMinutes(9), parsedPl.Entries[1].Duration, "second duration");

            var playerJson = """
            {
              "playabilityStatus": { "status": "OK" },
              "videoDetails": { "videoId": "abc12345678", "title": "Lesson 1: Intro", "author": "Arabic101", "lengthSeconds": "754" },
              "streamingData": {
                "formats": [
                  { "itag": 18, "url": "https://cdn.example/v360.mp4", "mimeType": "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"", "width": 640, "height": 360, "contentLength": "15000000", "qualityLabel": "360p" }
                ],
                "adaptiveFormats": [
                  { "itag": 137, "url": "https://cdn.example/v1080.mp4", "mimeType": "video/mp4; codecs=\"avc1.640028\"", "width": 1920, "height": 1080, "contentLength": "80000000", "qualityLabel": "1080p" },
                  { "itag": 140, "url": "https://cdn.example/a140.m4a", "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"", "contentLength": "6000000", "averageBitrate": 128000 }
                ]
              }
            }
            """;

            var info = YouTubeService.ParseNativePlayerJson(playerJson, "https://www.youtube.com/watch?v=abc12345678", "TestUA", hasFfmpeg: true, out var err);
            Assert.True(err is null && info is not null, "native player parsed cleanly");
            Assert.Equal("Lesson 1: Intro", info!.Title, "video title");
            Assert.True(info.QualityOptions.Any(q => q.Height == 1080 && q.NeedsAudioMerge), "1080p DASH enriched with m4a audio");
            Assert.True(info.QualityOptions.Any(q => q.Height == 360 && q.IsCombined), "360p combined present");
            Assert.True(info.AudioOptions.Any(a => a.Extension == "m4a"), "m4a audio option present");
            return Task.CompletedTask;
        }),

        new("playlist picker: allows selecting/deselecting individual videos before download starts", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            await rig.Ui.Run(() =>
            {
                var req = new ExtensionDownloadRequest
                {
                    Url = "https://www.youtube.com/playlist?list=PLpickerTest",
                    MediaType = "playlist",
                    PageTitle = "Tajweed Series",
                };
                var session = new DownloadSessionViewModel(req, env.Dir, rig.Vm.Loc, _ => null, (_, _) => Task.FromResult<DownloadItemViewModel?>(null));
                session.ApplyYouTubePlaylistProbe(new YouTubePlaylistInfo
                {
                    Id = "PLpickerTest",
                    Title = "Tajweed Series",
                    Entries =
                    [
                        new YouTubePlaylistEntry("vid00000001", "Episode 1", "https://www.youtube.com/watch?v=vid00000001", TimeSpan.FromMinutes(10)),
                        new YouTubePlaylistEntry("vid00000002", "Episode 2 (Skip me)", "https://www.youtube.com/watch?v=vid00000002", TimeSpan.FromMinutes(15)),
                        new YouTubePlaylistEntry("vid00000003", "Episode 3", "https://www.youtube.com/watch?v=vid00000003", TimeSpan.FromMinutes(8)),
                    ]
                });

                Assert.True(session.HasPlaylistEntries, "playlist entries loaded");
                Assert.Equal(3, session.SelectedPlaylistCount, "all 3 selected initially");
                Assert.True(session.IsAllPlaylistSelected, "select all checked initially");
                Assert.True(session.StartDownloadCommand.CanExecute(null), "start enabled");

                // Deselect the 2nd video
                session.PlaylistEntries[1].IsSelected = false;
                Assert.Equal(2, session.SelectedPlaylistCount, "2 selected after unchecking episode 2");
                Assert.True(!session.IsAllPlaylistSelected, "select all unchecked when partial");
                var picked = session.GetSelectedPlaylistEntries();
                Assert.Equal(2, picked.Count, "only 2 entries returned");
                Assert.True(picked.All(x => x.VideoId != "vid00000002"), "excluded skipped video");

                // Deselect all -> Start disabled
                session.IsAllPlaylistSelected = false;
                Assert.Equal(0, session.SelectedPlaylistCount, "0 selected");
                Assert.True(!session.StartDownloadCommand.CanExecute(null), "start disabled when 0 selected");

                // Re-select all -> Start re-enabled
                session.IsAllPlaylistSelected = true;
                Assert.Equal(3, session.SelectedPlaylistCount, "all 3 re-selected");
                Assert.True(session.StartDownloadCommand.CanExecute(null), "start re-enabled");
            });
        }),

        new("extension: server starts and responds to ping", async env =>
        {
            var port = 40000 + Random.Shared.Next(10000);
            await using var server = new ExtensionServer(port);
            server.Start();
            Assert.True(server.IsRunning, "server should be running");

            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://127.0.0.1:{port}/ping", env.Ct);
            Assert.True(resp.IsSuccessStatusCode, $"ping should succeed, got {resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync(env.Ct);
            Assert.True(body.Contains("Nahr"), "ping response should contain 'Nahr'");
        }),

        new("extension: server accepts /add and /prompt requests with media qualities", async env =>
        {
            var port = 40000 + Random.Shared.Next(10000);
            await using var server = new ExtensionServer(port);
            ExtensionDownloadRequest? received = null;
            var tcs = new TaskCompletionSource();
            server.DownloadRequested += (_, req) => { received = req; tcs.TrySetResult(); };
            server.Start();

            using var http = new HttpClient();
            var json = """
                {
                  "url": "https://example.com/video.mp4",
                  "pageTitle": "Demo Video",
                  "referrer": "https://example.com/watch",
                  "mediaType": "video",
                  "qualities": [
                    { "label": "720p", "url": "https://example.com/video720.mp4", "extension": "mp4" }
                  ]
                }
                """;
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://127.0.0.1:{port}/prompt", content, env.Ct);

            Assert.True(resp.IsSuccessStatusCode, $"prompt should succeed, got {resp.StatusCode}");

            await Task.WhenAny(tcs.Task, Task.Delay(2000, env.Ct));
            Assert.NotNull(received, "DownloadRequested should fire");
            Assert.Equal("https://example.com/video.mp4", received!.Url, "url");
            Assert.Equal("Demo Video", received.PageTitle, "pageTitle");
            Assert.Equal("video", received.MediaType, "mediaType");
            Assert.Equal(1, received.Qualities!.Count, "qualities parsed");
        }),

        new("extension: server rejects invalid requests", async env =>
        {
            var port = 40000 + Random.Shared.Next(10000);
            await using var server = new ExtensionServer(port);
            server.Start();

            using var http = new HttpClient();

            var resp1 = await http.PostAsync($"http://127.0.0.1:{port}/add",
                new StringContent("""{"filename":"test"}""", Encoding.UTF8, "application/json"), env.Ct);
            Assert.Equal(400, (int)resp1.StatusCode, "missing url should be 400");

            var resp2 = await http.PostAsync($"http://127.0.0.1:{port}/add",
                new StringContent("not json", Encoding.UTF8, "application/json"), env.Ct);
            Assert.Equal(400, (int)resp2.StatusCode, "invalid json should be 400");

            var resp3 = await http.PostAsync($"http://127.0.0.1:{port}/add",
                new StringContent("""{"url":"ftp://example.com/file"}""", Encoding.UTF8, "application/json"), env.Ct);
            Assert.Equal(400, (int)resp3.StatusCode, "ftp url should be 400");

            var resp4 = await http.GetAsync($"http://127.0.0.1:{port}/unknown", env.Ct);
            Assert.Equal(404, (int)resp4.StatusCode, "unknown path should be 404");
        }),

        new("extension: server handles CORS preflight", async env =>
        {
            var port = 40000 + Random.Shared.Next(10000);
            await using var server = new ExtensionServer(port);
            server.Start();

            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{port}/prompt");
            req.Headers.Add("Origin", "chrome-extension://abcdefgh");
            var resp = await http.SendAsync(req, env.Ct);
            Assert.Equal(204, (int)resp.StatusCode, "OPTIONS should return 204");
            Assert.True(resp.Headers.Contains("Access-Control-Allow-Origin"), "should have CORS header");
        }),

        new("extension: server queues and drains browser fallback URLs on HTTP 403", async env =>
        {
            var port = 40000 + Random.Shared.Next(10000);
            await using var server = new ExtensionServer(port);
            server.Start();

            server.QueueBrowserFallback("https://onlinelibrary.wiley.com/doi/pdfdirect/10.1111/epi.18701", "epi.18701.pdf");

            using var http = new HttpClient();
            var first = await http.GetStringAsync($"http://127.0.0.1:{port}/fallbacks", env.Ct);
            Assert.True(first.Contains("onlinelibrary.wiley.com", StringComparison.Ordinal), $"expected queued host: {first}");
            Assert.True(first.Contains("epi.18701", StringComparison.Ordinal), $"expected queued url: {first}");

            var second = await http.GetStringAsync($"http://127.0.0.1:{port}/fallbacks", env.Ct);
            Assert.Equal("""{"items":[]}""", second, "second drain should be empty");
        }),
    ];
}
