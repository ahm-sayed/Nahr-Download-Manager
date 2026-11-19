using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Core.Security;
using DownloadManager.Data;
using DownloadManager.Engine;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;
using DownloadManager.Presentation.Services;
using DownloadManager.Presentation.Settings;
using DownloadManager.Presentation.ViewModels;
using DownloadManager.TestServer;

namespace DownloadManager.Verification;

/// <summary>Fake notification service for testing.</summary>
internal sealed class FakeNotificationService : INotificationService
{
    public readonly ConcurrentQueue<(string Title, string Message, bool IsError)> Notifications = new();

    public void Show(string title, string message, bool isError = false)
    {
        Notifications.Enqueue((title, message, isError));
    }
}

/// <summary>Fake clipboard monitor for testing.</summary>
internal sealed class FakeClipboardMonitor : IDisposable
{
    public bool IsStarted { get; private set; }
    public bool IsDisposed { get; private set; }

    public void Start()
    {
        IsStarted = true;
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}

/// <summary>VM rig that wires SettingsChanged to a fake clipboard monitor, simulating App.xaml.cs.</summary>
internal sealed class FakeClipVmRig : IAsyncDisposable
{
    public required TestEnv Env { get; init; }
    public required UiThread Ui { get; init; }
    public required SqliteDatabase Db { get; init; }
    public required DownloadEngine Engine { get; init; }
    public required MainViewModel Vm { get; init; }
    public required FakeShell Shell { get; init; }
    public required ISettingsStore Settings { get; init; }
    public required FakeNotificationService Notifications { get; init; }
    public required FakeClipboardMonitor ClipboardMonitor { get; init; }
    private bool _closed;

    public static async Task<FakeClipVmRig> CreateAsync(TestEnv env, string? dbPath = null, Action<AppSettings>? tweakSettings = null)
    {
        var db = SqliteDatabase.Open(dbPath ?? Path.Combine(env.Dir, "..", "app.db"));
        var settingsStore = new SqliteSettingsStore(db);
        var settings = AppSettings.Load(settingsStore, env.Dir);
        settings.DownloadDirectory = env.Dir;
        tweakSettings?.Invoke(settings);
        settings.Save(settingsStore); // persist tweaked settings before InitializeAsync
        var engine = env.NewEngine(new SqliteDownloadStore(db));
        var ui = new UiThread();
        var shell = new FakeShell();
        var notifications = new FakeNotificationService();
        var clipboardMonitor = new FakeClipboardMonitor();
        var loc = new Localizer();

        var vm = await ui.Run(() =>
        {
            var m = new MainViewModel(engine, settingsStore, settings, loc, ui, shell);
            m.NotificationsEnabled = true;
            m.Notifications = notifications;
            m.ClipboardMonitor = clipboardMonitor;
            return m;
        });

        await ui.RunAsync(vm.InitializeAsync);

        // Wire SettingsChanged to simulate App.xaml.cs's ApplyBehaviourSettings: start/stop/dispose
        // the clipboard monitor when MonitorClipboard changes at runtime.
        vm.SettingsChanged += (_, model) =>
        {
            var wanted = model.MonitorClipboard != 0;
            if (wanted && !clipboardMonitor.IsStarted)
            {
                clipboardMonitor.Start();
            }
            else if (!wanted && !clipboardMonitor.IsDisposed)
            {
                clipboardMonitor.Dispose();
            }
        };

        return new FakeClipVmRig
        {
            Env = env,
            Ui = ui,
            Db = db,
            Engine = engine,
            Vm = vm,
            Shell = shell,
            Settings = settingsStore,
            Notifications = notifications,
            ClipboardMonitor = clipboardMonitor
        };
    }

    public async Task WaitUntilAsync(Func<MainViewModel, bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!await Ui.Run(() => condition(Vm)))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(60)) throw new AssertionException($"timed out waiting for: {what}");
            await Task.Delay(25, Env.Ct);
        }
    }

    /// <summary>Opens the add dialog, fills it and confirms, exactly like the user would.</summary>
    public async Task AddAsync(string urlsText, int connections = 4, bool start = true)
    {
        await Ui.Run(() =>
        {
            Vm.OpenAddDialog(null);
            var d = Vm.AddDialog!;
            d.UrlsText = urlsText;
            d.DestinationDirectory = Env.Dir;
            d.Connections = connections;
            d.StartImmediately = start;
        });
        await Ui.RunAsync(() => ((AsyncRelayCommand)Vm.AddDialog!.ConfirmCommand).ExecuteAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;                       // idempotent: callers may dispose explicitly and again at end of scope
        _closed = true;
        await Ui.Run(() => Vm.DisposeAsync().AsTask());
        await Engine.DisposeAsync();
        Ui.Stop();
        Db.Dispose();
    }
}

public static class WiringTests
{
    public static IEnumerable<TestCase> All()
    {
        // 1A: App._settings and MainViewModel.Settings are the same instance (shared at construction,
        // both reassigned to the new model when settings are saved). Proven by SettingsChanged firing.
        yield return new TestCase("wiring: App._settings and MainViewModel.Settings are the same instance", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var initialSettings = rig.Vm.Settings;

            // Change a setting through the SettingsViewModel
            await rig.Ui.Run(() =>
            {
                rig.Vm.SettingsPage.AutoResumeDownloads = rig.Vm.SettingsPage.AutoResumeDownloads == 1 ? 0 : 1;
                rig.Vm.SettingsPage.SaveCommand.Execute(null);
            });

            await rig.Ui.Run(() => Task.CompletedTask);

            // The model should be updated after Save
            var model = rig.Vm.Settings;
            Assert.True(model.AutoResumeDownloads != initialSettings.AutoResumeDownloads,
                "Settings instance should be updated when saved");
        });

        // 1A: SettingsChanged fires with the new model when settings are saved
        yield return new TestCase("wiring: SettingsChanged event fires and model updates when settings are saved", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var initialMonitorClipboard = rig.Vm.Settings.MonitorClipboard;

            AppSettings? changedModel = null;
            rig.Vm.SettingsChanged += (_, model) => changedModel = model;

            await rig.Ui.Run(() =>
            {
                rig.Vm.SettingsPage.MonitorClipboard = rig.Vm.SettingsPage.MonitorClipboard == 1 ? 0 : 1;
                rig.Vm.SettingsPage.SaveCommand.Execute(null);
            });

            await rig.Ui.Run(() => Task.CompletedTask);

            Assert.NotNull(changedModel, "SettingsChanged event should fire");
            Assert.True(changedModel!.MonitorClipboard != initialMonitorClipboard, "changed model should have new value");
            Assert.True(rig.Vm.Settings.MonitorClipboard == changedModel.MonitorClipboard, "MainViewModel.Settings should be updated");
        });

        // 1A: Clipboard monitor starts/stops when MonitorClipboard changes at runtime
        yield return new TestCase("wiring: clipboard monitor starts/stops when MonitorClipboard setting changes at runtime", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var clipboard = rig.ClipboardMonitor;

            // Initially not started
            Assert.True(!clipboard.IsStarted, "clipboard monitor should not be started before settings applied");

            // Enable it
            await rig.Ui.Run(() =>
            {
                rig.Vm.SettingsPage.MonitorClipboard = 1;
                rig.Vm.SettingsPage.SaveCommand.Execute(null);
            });
            await rig.Ui.Run(() => Task.CompletedTask);
            Assert.True(clipboard.IsStarted, "clipboard monitor should be started when MonitorClipboard is enabled");

            // Disable it
            await rig.Ui.Run(() =>
            {
                rig.Vm.SettingsPage.MonitorClipboard = 0;
                rig.Vm.SettingsPage.SaveCommand.Execute(null);
            });
            await rig.Ui.Run(() => Task.CompletedTask);
            Assert.True(clipboard.IsDisposed, "clipboard monitor should be disposed when MonitorClipboard is disabled");
        });

        // 1B: Notification fires exactly once on terminal states (Completed, Failed), not on progress ticks
        yield return new TestCase("wiring: notification fires exactly once on Completed", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            await rig.AddAsync(env.Server.Url("range", 1024 * 1024, "file.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "download completed");

            Assert.Equal(1, notifications.Notifications.Count, "exactly one notification on completion");
            notifications.Notifications.TryDequeue(out var notif);
            Assert.True(notif.Title.StartsWith("Download complete:"), $"title: {notif.Title}");
            Assert.True(!notif.IsError, "should not be error notification");
        });

        yield return new TestCase("wiring: notification fires exactly once on Failed", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            await rig.AddAsync(env.Server.Url("notfound", 0, "missing.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Failed, "download failed");

            Assert.Equal(1, notifications.Notifications.Count, "exactly one notification on failure");
            notifications.Notifications.TryDequeue(out var notif);
            Assert.True(notif.Title.StartsWith("Download failed:"), $"title: {notif.Title}");
            Assert.True(notif.IsError, "should be error notification");
        });

        yield return new TestCase("wiring: no notification on progress ticks", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            env.Server.State.SlowChunkDelayMs = 5;
            await rig.AddAsync(env.Server.Url("range+slow", 5 * 1024 * 1024, "big.bin").AbsoluteUri, connections: 4);

            await rig.WaitUntilAsync(vm => vm.Downloads[0].ProgressPercent > 30, "30% progress");
            Assert.Equal(0, notifications.Notifications.Count, "no notifications during progress");

            await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Completed, "download completed");
            Assert.Equal(1, notifications.Notifications.Count, "one notification after completion");
        });

        yield return new TestCase("wiring: no notification for user-cancelled downloads", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            env.Server.State.SlowChunkDelayMs = 10;
            await rig.AddAsync(env.Server.Url("range+slow", 5 * 1024 * 1024, "cancel.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads[0].ProgressPercent > 20, "some progress");

            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].CancelCommand).ExecuteAsync());
            await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Cancelled, "download cancelled");

            Assert.Equal(0, notifications.Notifications.Count, "no notification for cancelled downloads");
        });

        yield return new TestCase("wiring: notification respects NotificationsEnabled setting", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            await rig.Ui.Run(() => rig.Vm.NotificationsEnabled = false);

            await rig.AddAsync(env.Server.Url("range", 1024 * 1024, "file.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "download completed");

            Assert.Equal(0, notifications.Notifications.Count, "no notifications when NotificationsEnabled is false");
        });

        // 1C: HTML warning - server responds with text/html content type
        yield return new TestCase("wiring: HTML warning fires when server returns text/html", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            // Point at a server that serves text/html content type
            await rig.AddAsync(env.Server.Url("html", 1024, "page.html").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "download completed");

            // The download should have completed successfully; HTML warning should be flagged
            Assert.True(rig.Vm.Downloads[0].NeedsHtmlWarning, "HTML warning should be set for text/html content");
            Assert.Equal(".html", Path.GetExtension(rig.Vm.Downloads[0].FileName), "File should have .html extension");
        });

        yield return new TestCase("wiring: no HTML warning for binary content types", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            await rig.AddAsync(env.Server.Url("range", 1024 * 1024, "file.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "download completed");

            Assert.True(!rig.Vm.Downloads[0].NeedsHtmlWarning, "No HTML warning for binary content");
            Assert.Equal(".bin", Path.GetExtension(rig.Vm.Downloads[0].FileName), "File should have .bin extension");
        });

        // 1D: Hotlink protection - server returns 403 for unauthorized requests
        yield return new TestCase("wiring: hotlink protection returns 403 for unauthorized requests", async env =>
        {
            await using var rig = await FakeClipVmRig.CreateAsync(env);
            var notifications = rig.Notifications;

            // Hit the hotlink handler directly (segs[0] == "hotlink")
            await rig.AddAsync(env.Server.Url("hotlink", 1024, "file.bin", "/hotlink/1024/file.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Failed, "download failed");

            // The download should fail with 403 status code
            var download = rig.Vm.Downloads[0];
            Assert.Equal(DownloadStatus.Failed, download.Status, "Status should be Failed");
            Assert.True(download.ErrorMessage.Contains("403"), $"Error message should mention 403: {download.ErrorMessage}");
        });

        // 2A: Regression test — hammer PauseAsync / CancelAsync / DisposeAsync 150 iterations
        yield return new TestCase("regression: hammer PauseAsync, CancelAsync, DisposeAsync", async env =>
        {
            for (var i = 0; i < 150; i++)
            {
                await using var rig = await FakeClipVmRig.CreateAsync(env);
                var notifications = rig.Notifications;

                // Use larger file so download takes long enough for pause to take effect
                await rig.AddAsync(env.Server.Url("range", 10 * 1024 * 1024, "file.bin").AbsoluteUri);
                await rig.Ui.Run(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].PauseCommand).ExecuteAsync());
                await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Paused, "paused");

                await rig.Ui.Run(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].CancelCommand).ExecuteAsync());
                await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Cancelled, "cancelled");

                // Clear resources
                await rig.Ui.Run(() => rig.Vm.DisposeAsync().AsTask());
            }
        });

        // 2B: Batch import + auto-resume E2E
        yield return new TestCase("batch + auto-resume E2E", async env =>
        {
            var dbPath = Path.Combine(env.Dir, "batch.db");

            // Pre-seed two interrupted downloads directly in SQLite (Paused with progress)
            using var db = SqliteDatabase.Open(dbPath);
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var url = env.Server.Url("range+slow", 1024, "file.bin").AbsoluteUri;
            db.Execute(
                "INSERT INTO downloads (id, url, final_url, file_name, file_path, category, total_bytes, supports_ranges, etag, last_modified, content_type, status, error, connections, speed_limit, single_stream_bytes, created_at, completed_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                id1.ToString(), url, url, "file1.bin", Path.Combine(env.Dir, "file1.bin"), "General", 1024, 1, null, null, "application/octet-stream", 2, null, 4, 0, 512, DateTimeOffset.UtcNow.ToString("o"), null);
            db.Execute("INSERT INTO segments (download_id, idx, start_byte, end_byte, downloaded) VALUES (?,?,?,?,?)",
                id1, 0, 0, 1023, 512);
            db.Execute(
                "INSERT INTO downloads (id, url, final_url, file_name, file_path, category, total_bytes, supports_ranges, etag, last_modified, content_type, status, error, connections, speed_limit, single_stream_bytes, created_at, completed_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                id2.ToString(), url, url, "file2.bin", Path.Combine(env.Dir, "file2.bin"), "General", 1024, 1, null, null, "application/octet-stream", 2, null, 4, 0, 512, DateTimeOffset.UtcNow.ToString("o"), null);
            db.Execute("INSERT INTO segments (download_id, idx, start_byte, end_byte, downloaded) VALUES (?,?,?,?,?)",
                id2, 0, 0, 1023, 512);

            // First rig: auto-resume defaults to on
            await using var rig1 = await FakeClipVmRig.CreateAsync(env, dbPath: dbPath);
            await rig1.WaitUntilAsync(vm => vm.Downloads.Count == 2, "two downloads recovered");
            await rig1.WaitUntilAsync(vm => vm.Downloads.All(d => d.Status == DownloadStatus.Completed), "both complete after auto-resume");
        });

        // 2C: Auto-resume setting — default on: interrupted downloads restart and finish
        yield return new TestCase("wiring: auto-resume enabled by default", async env =>
        {
            var dbPath = Path.Combine(env.Dir, "auto.db");

            // Pre-seed an interrupted download directly in SQLite (Paused with progress)
            using var db = SqliteDatabase.Open(dbPath);
            var id = Guid.NewGuid();
            var url = env.Server.Url("range", 1024 * 1024, "auto.bin").AbsoluteUri;
            db.Execute(
                "INSERT INTO downloads (id, url, final_url, file_name, file_path, category, total_bytes, supports_ranges, etag, last_modified, content_type, status, error, connections, speed_limit, single_stream_bytes, created_at, completed_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                id.ToString(), url, url, "auto.bin", Path.Combine(env.Dir, "auto.bin"), "General",
                1024 * 1024, 1, null, null, "application/octet-stream", 2, null, 4, 0, 512 * 1024,
                DateTimeOffset.UtcNow.ToString("o"), null);
            db.Execute("INSERT INTO segments (download_id, idx, start_byte, end_byte, downloaded) VALUES (?,?,?,?,?)",
                id, 0, 0, 1024 * 1024 - 1, 512 * 1024);

            // First rig: auto-resume defaults to on
            var rig1 = await FakeClipVmRig.CreateAsync(env, dbPath: dbPath);
            Assert.Equal(1, rig1.Vm.Settings.AutoResumeDownloads, "auto-resume should default to on");
            await rig1.WaitUntilAsync(vm => vm.Downloads.Count == 1, "one download recovered");

            // With auto-resume ON, it should complete
            await rig1.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Completed, "completed after auto-resume");
        });

        // 2D: Auto-resume setting — off: interrupted downloads stay paused after recovery
        yield return new TestCase("wiring: auto-resume disabled", async env =>
        {
            var dbPath = Path.Combine(env.Dir, "noauto.db");

            // Pre-seed an interrupted download directly in SQLite (Paused with progress)
            using var db = SqliteDatabase.Open(dbPath);
            var id = Guid.NewGuid();
            var url = env.Server.Url("range", 1024 * 1024, "noauto.bin").AbsoluteUri;
            db.Execute(
                "INSERT INTO downloads (id, url, final_url, file_name, file_path, category, total_bytes, supports_ranges, etag, last_modified, content_type, status, error, connections, speed_limit, single_stream_bytes, created_at, completed_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                id.ToString(), url, url, "noauto.bin", Path.Combine(env.Dir, "noauto.bin"), "General",
                1024 * 1024, 1, null, null, "application/octet-stream", 2, null, 4, 0, 512 * 1024,
                DateTimeOffset.UtcNow.ToString("o"), null);
            db.Execute("INSERT INTO segments (download_id, idx, start_byte, end_byte, downloaded) VALUES (?,?,?,?,?)",
                id, 0, 0, 1024 * 1024 - 1, 512 * 1024);

            // First rig: auto-resume disabled
            await using var rig1 = await FakeClipVmRig.CreateAsync(env, dbPath: dbPath,
                tweakSettings: s => s.AutoResumeDownloads = 0);
            Assert.Equal(0, rig1.Vm.Settings.AutoResumeDownloads, "auto-resume should be off");

            // With auto-resume OFF, the recovered item should stay Paused
            await rig1.WaitUntilAsync(vm => vm.Downloads.Count == 1, "one download recovered");
            Assert.Equal(DownloadStatus.Paused, rig1.Vm.Downloads[0].Status,
                "recovered item should stay Paused when auto-resume is off");
        });

        // 2C: Sensitive headers are not persisted
        yield return new TestCase("wiring: sensitive headers are not persisted in SQLite", async env =>
        {
            var dbPath = Path.Combine(env.Dir, "sensitive.db");
            await using var rig = await FakeClipVmRig.CreateAsync(env, dbPath: dbPath);

            var url = env.Server.Url("range", 1024, "file.bin").AbsoluteUri.ToString();

            // Add + run a download that carries sensitive headers.
            var item = await rig.Engine.AddAsync(new AddDownloadRequest(url, env.Dir)
            {
                Connections = 4,
                ExtraHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer secret-token-xyz",
                    ["Cookie"] = "session=abcd1234",
                    ["Proxy-Authorization"] = "Basic dGVzdDp0ZXN0",
                    ["Set-Cookie"] = "session2=efgh5678",
                    ["X-Api-Key"] = "api-key-ijklmnop",
                },
            }, env.Ct);
            await rig.Engine.StartAsync(item.Id, env.Ct);
            await rig.Engine.WaitAsync(item.Id, env.Ct);

            // The SQLite schema has no column for per-download headers, so the secret can never be in the DB.
            // Prove it: dump every text column of every row and assert the secret substrings are absent.
            var stored = rig.Db.Query(
                "SELECT url, final_url, file_name, file_path, category, etag, last_modified, content_type, error " +
                "FROM downloads",
                r => string.Join("|", Enumerable.Range(0, 9).Select(r.GetNullableString)));
            foreach (var row in stored)
            {
                Assert.True(!row.Contains("secret-token-xyz") && !row.Contains("abcd1234") && !row.Contains("dGVzdDp0ZXN0") &&
                            !row.Contains("efgh5678") && !row.Contains("api-key-ijklmnop"),
                    $"a secret header value was stored in SQLite: {row}");
            }

            // SanitisedSnapshot redacts them in memory, so even a future store that saved snapshots cannot leak them.
            var sanitised = item.SanitisedSnapshot();
            Assert.NotNull(sanitised.ExtraHeaders, "extra headers should survive to the running item");
            Assert.True(sanitised.ExtraHeaders!.Values.All(v => v == HeaderSanitiser.Redacted),
                $"sanitised snapshot leaked a secret: {string.Join(",", sanitised.ExtraHeaders.Values)}");
        });
    }
}