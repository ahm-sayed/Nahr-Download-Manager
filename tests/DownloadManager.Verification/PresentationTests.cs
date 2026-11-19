using System.Collections.Concurrent;
using System.Diagnostics;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Data;
using DownloadManager.Engine;
using DownloadManager.Presentation.Formatting;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;
using DownloadManager.Presentation.Services;
using DownloadManager.Presentation.Settings;
using DownloadManager.Presentation.ViewModels;

namespace DownloadManager.Verification;

/// <summary>A single thread with a SynchronizationContext: behaves like the WPF dispatcher for the view-models.</summary>
internal sealed class UiThread : SynchronizationContext, IUiDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();

    public UiThread()
    {
        var t = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var a in _queue.GetConsumingEnumerable()) a();
        }) { IsBackground = true, Name = "fake-ui" };
        t.Start();
    }

    public void Post(Action action) => _queue.Add(action);
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add(() => d(state));

    public Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }

    public Task Run(Action a) => Run(() => { a(); return 0; });

    public Task RunAsync(Func<Task> f)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () => { try { await f(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }

    public void Stop() => _queue.CompleteAdding();
}

internal sealed class FakeShell : IShellService
{
    public ConcurrentQueue<string> Opened { get; } = new();
    public ConcurrentQueue<string> Folders { get; } = new();
    public bool ConfirmResult { get; set; } = true;
    public string? Picked { get; set; }
    public void OpenFile(string path) => Opened.Enqueue(path);
    public void OpenFolder(string path) => Folders.Enqueue(path);
    public string? PickFolder(string? initialDirectory) => Picked;
    public bool Confirm(string message) => ConfirmResult;
}

internal sealed class VmRig : IAsyncDisposable
{
    public required TestEnv Env { get; init; }
    public required UiThread Ui { get; init; }
    public required SqliteDatabase Db { get; init; }
    public required DownloadEngine Engine { get; init; }
    public required MainViewModel Vm { get; init; }
    public required FakeShell Shell { get; init; }
    public required ISettingsStore Settings { get; init; }

    public static async Task<VmRig> CreateAsync(TestEnv env, string? dbPath = null, Action<AppSettings>? tweakSettings = null)
    {
        var db = SqliteDatabase.Open(dbPath ?? Path.Combine(env.Dir, "..", "app.db"));
        var settingsStore = new SqliteSettingsStore(db);
        var settings = AppSettings.Load(settingsStore, env.Dir);
        settings.DownloadDirectory = env.Dir;
        tweakSettings?.Invoke(settings);
        var engine = env.NewEngine(new SqliteDownloadStore(db));
        var ui = new UiThread();
        var shell = new FakeShell();
        var loc = new Localizer();
        var vm = await ui.Run(() => new MainViewModel(engine, settingsStore, settings, loc, ui, shell));
        await ui.RunAsync(vm.InitializeAsync);
        return new VmRig { Env = env, Ui = ui, Db = db, Engine = engine, Vm = vm, Shell = shell, Settings = settingsStore };
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
        await Ui.Run(() => Vm.DisposeAsync().AsTask());
        await Engine.DisposeAsync();
        Ui.Stop();
        Db.Dispose();
    }
}

public static class PresentationTests
{
    private const long MiB = 1024 * 1024;

    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("ui: formatting of sizes, speeds, ETA and percentages", _ =>
        {
            Assert.Equal("0 B", Format.Bytes(0), "0"); Assert.Equal("1023 B", Format.Bytes(1023), "1023");
            Assert.Equal("1.0 KB", Format.Bytes(1024), "1 KB"); Assert.Equal("1.5 MB", Format.Bytes(1_572_864), "1.5 MB");
            Assert.Equal("5.0 GB", Format.Bytes(5L * 1024 * 1024 * 1024), "5 GB");
            Assert.Equal("—", Format.Speed(0), "no speed"); Assert.Equal("2.0 MB/s", Format.Speed(2 * MiB), "speed");
            Assert.Equal("—", Format.Eta(null), "no eta"); Assert.Equal("12s", Format.Eta(TimeSpan.FromSeconds(11.2)), "seconds");
            Assert.Equal("3m 05s", Format.Eta(TimeSpan.FromSeconds(185)), "minutes"); Assert.Equal("1h 02m", Format.Eta(TimeSpan.FromMinutes(62)), "hours");
            Assert.Equal("2d 03h", Format.Eta(TimeSpan.FromHours(51)), "days"); Assert.Equal("100%", Format.Percent(150), "clamped");
            Assert.Equal("Compressed", Categorizer.ForFileName("A.ZIP"), "zip"); Assert.Equal("Documents", Categorizer.ForFileName("x.pdf"), "pdf");
            Assert.Equal("Video", Categorizer.ForFileName("m.MKV"), "mkv"); Assert.Equal("General", Categorizer.ForFileName("noext"), "none");
            Assert.Equal("General", Categorizer.ForFileName(null), "null");
            return Task.CompletedTask;
        });

        yield return new TestCase("ui: Arabic/English tables are complete and the language switches live with RTL", _ =>
        {
            var en = Strings.En.Keys.OrderBy(k => k).ToList();
            var ar = Strings.Ar.Keys.OrderBy(k => k).ToList();
            Assert.True(en.SequenceEqual(ar), "missing keys: " + string.Join(",", en.Except(ar).Concat(ar.Except(en))));
            foreach (var k in en)
                Assert.Equal(Strings.En[k].Split("{0}").Length, Strings.Ar[k].Split("{0}").Length, $"placeholder count of '{k}'");
            foreach (var c in Categorizer.All) Assert.True(Strings.En.ContainsKey("cat_" + c), $"category label {c}");
            foreach (var s in Enum.GetNames<DownloadStatus>()) Assert.True(Strings.En.ContainsKey("status_" + s), $"status label {s}");

            var loc = new Localizer();
            var changed = new List<string?>();
            loc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.True(!loc.IsRtl && loc["add_url"] == "Add URL", "starts in English LTR");
            loc.Language = "ar";
            Assert.True(loc.IsRtl && loc["add_url"] == "إضافة رابط", "Arabic and RTL");
            Assert.True(changed.Contains("Item[]") && changed.Contains("IsRtl"), "bindings are notified: " + string.Join(",", changed));
            Assert.Equal("لم يتم العثور على روابط http(s) صالحة.", loc["add_no_valid"], "message in Arabic");
            Assert.Equal("missing_key", loc["missing_key"], "unknown keys fall back to the key");
            Assert.Equal("تم تخطي 3 سطر غير صالح.", loc.Format("add_skipped", 3), "format");
            loc.Language = "xx";
            Assert.True(!loc.IsRtl, "unknown language falls back to English");
            return Task.CompletedTask;
        });

        yield return new TestCase("ui: batch URL parsing keeps distinct valid http(s) links only", env =>
        {
            var urls = AddDownloadViewModel.ParseUrls("""
                https://a.example/x.zip
                http://b.example/y.pdf   https://a.example/x.zip
                ftp://c.example/z
                not a link
                https://user:pw@d.example/secret
                javascript:alert(1)
                """, out var skipped);
            Assert.Equal(2, urls.Count, "valid links");
            Assert.Equal(6, skipped, "skipped tokens (ftp, 'not','a','link', credentials url, javascript)");
            Assert.Equal(0, AddDownloadViewModel.ParseUrls(null, out _).Count, "null text");
            return Task.CompletedTask;
        });

        yield return new TestCase("ui: commands cannot re-enter and report errors instead of crashing", async _ =>
        {
            var gate = new TaskCompletionSource();
            var runs = 0;
            var cmd = new AsyncRelayCommand(async () => { runs++; await gate.Task; });
            var first = cmd.ExecuteAsync();
            await cmd.ExecuteAsync();                                   // ignored while the first one runs
            Assert.True(runs == 1 && !cmd.CanExecute(null), "second invocation ignored while running");
            gate.SetResult();
            await first;
            Assert.True(cmd.CanExecute(null), "enabled again afterwards");

            Exception? reported = null;
            AsyncRelayCommand.Error = e => reported = e;
            await new AsyncRelayCommand(() => throw new InvalidOperationException("boom")).ExecuteAsync();
            AsyncRelayCommand.Error = null;
            Assert.True(reported?.Message == "boom", "exception reported through the Error hook");
        });

        yield return new TestCase("ui: batch add -> list, categories, filters, search, buttons, clear (real engine + SQLite)", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            var a = env.Server.Url("range", 600_000, "a.zip").AbsoluteUri;
            var b = env.Server.Url("range", 400_000, "b.pdf").AbsoluteUri;
            var c = env.Server.Url("range", 300_000, "c.mp3").AbsoluteUri;
            await rig.AddAsync($"{a}\n{b}\n{a}\n{c}\nnot-a-link\n");
            await rig.WaitUntilAsync(vm => vm.Downloads.Count(d => d.Status == DownloadStatus.Completed) == 3, "3 completed downloads");
            await env.Server.State.WaitForIdleAsync(env.Ct);

            await rig.Ui.Run(() =>
            {
                var vm = rig.Vm;
                Assert.Equal(3, vm.Downloads.Count, "duplicate link added once");
                Assert.True(vm.StatusMessage.Contains("1 invalid line(s) were skipped."), $"status message: '{vm.StatusMessage}'");
                var sizes = new Dictionary<string, (long Size, string Cat)> { ["a.zip"] = (600_000, "Compressed"), ["b.pdf"] = (400_000, "Documents"), ["c.mp3"] = (300_000, "Music") };
                foreach (var d in vm.Downloads)
                {
                    var (size, cat) = sizes[d.FileName];
                    Assert.Equal(cat, d.CategoryKey, d.FileName + " category");
                    Assert.Equal(100.0, d.ProgressPercent, d.FileName + " progress");
                    Assert.Equal(Format.Bytes(size), d.ProgressText, d.FileName + " text");
                    Assert.FileMatches(rig.Engine.Get(d.Id)!, size);
                    Assert.True(!d.CanStart && !d.CanPause && !d.CanCancel && d.CanOpen, d.FileName + " button states");
                }

                vm.SelectedFilter = vm.Filters.Single(f => f.Key == "completed"); Assert.Equal(3, vm.VisibleDownloads.Count, "completed filter");
                vm.SelectedFilter = vm.Filters.Single(f => f.Key == "failed"); Assert.Equal(0, vm.VisibleDownloads.Count, "failed filter");
                vm.SelectedFilter = vm.Filters.Single(f => f.Key == "active"); Assert.Equal(0, vm.VisibleDownloads.Count, "active filter");
                vm.SelectedFilter = vm.Filters[0];
                Assert.Equal(3, vm.Filters.Single(f => f.Key == "completed").Count, "completed counter");
                Assert.Equal(1, vm.Categories.Single(x => x.Key == "Compressed").Count, "category counter");

                vm.SelectedCategory = vm.Categories.Single(x => x.Key == "Music");
                Assert.True(vm.VisibleDownloads.Count == 1 && vm.VisibleDownloads[0].FileName == "c.mp3", "category filter");
                vm.SelectedCategory = vm.Categories[0];
                vm.SearchText = "B.PDF"; Assert.Equal(1, vm.VisibleDownloads.Count, "search is case-insensitive");
                vm.SearchText = "zzz"; Assert.Equal(0, vm.VisibleDownloads.Count, "search with no match");
                vm.SearchText = ""; Assert.Equal(3, vm.VisibleDownloads.Count, "cleared search");

                var first = vm.Downloads[0];
                first.OpenCommand.Execute(null);
                Assert.Equal(first.FilePath, rig.Shell.Opened.Single(), "Open uses the shell");
                first.OpenFolderCommand.Execute(null);
                Assert.Equal(1, rig.Shell.Folders.Count, "Open folder uses the shell");
            });

            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.ClearCompletedCommand).ExecuteAsync());
            await rig.Ui.Run(() =>
            {
                Assert.True(rig.Vm.Downloads.Count == 0 && rig.Vm.VisibleDownloads.Count == 0 && rig.Vm.ShowEmptyState, "list cleared, empty state shown");
                Assert.Equal(0, rig.Engine.GetAll().Count, "engine forgot them");
                Assert.Equal(3, Directory.GetFiles(env.Dir).Length, "files stay on disk after clearing the list");
            });
            Assert.Equal(0, (await new SqliteDownloadStore(rig.Db).ListAsync()).Count, "history removed from the database");
        });

        yield return new TestCase("ui: live progress, pause/resume buttons, segment details, delete with confirmation", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 5 * MiB;
            await using var rig = await VmRig.CreateAsync(env);
            await rig.AddAsync(env.Server.Url("range+slow", size, "big.bin").AbsoluteUri, connections: 4);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].ProgressPercent > 20, "20% progress");

            await rig.Ui.Run(() =>
            {
                var d = rig.Vm.Downloads[0];
                rig.Vm.SelectedDownload = d;
                Assert.True(d.IsDownloading && d.CanPause && d.ShowPause && !d.ShowStart, "pause button shown while downloading");
                Assert.True(d.SpeedText.EndsWith("/s") && rig.Vm.TotalSpeedText.EndsWith("/s"), $"speed shown: '{d.SpeedText}' / '{rig.Vm.TotalSpeedText}'");
                Assert.Equal(1, rig.Vm.ActiveCount, "active counter");
                Assert.Equal(4, d.Segments.Count, "segment details");
                Assert.True(d.Segments.Any(s => s.Percent > 0), "segment progress visible");
                Assert.Equal(4, d.Connections, "connections");
                Assert.True(d.SupportsRanges && d.RangesText == "Yes", "resumable flag");
            });

            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].PauseCommand).ExecuteAsync());
            await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Paused, "Paused");
            await rig.Ui.Run(() =>
            {
                var d = rig.Vm.Downloads[0];
                Assert.True(d.ShowStart && !d.ShowPause && d.PrimaryActionText == "Resume", "resume button replaces pause");
                Assert.True(d.ProgressPercent > 0 && d.ProgressPercent < 100 && d.SpeedText == "", "partial progress kept, no speed while paused");
                rig.Vm.SelectedFilter = rig.Vm.Filters.Single(f => f.Key == "paused");
                Assert.Equal(1, rig.Vm.VisibleDownloads.Count, "paused filter");
                rig.Vm.SelectedFilter = rig.Vm.Filters[0];
            });

            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].StartCommand).ExecuteAsync());
            await rig.WaitUntilAsync(vm => vm.Downloads[0].Status == DownloadStatus.Completed, "Completed after resume");
            var path = await rig.Ui.Run(() => rig.Vm.Downloads[0].FilePath);
            Assert.FileMatches(rig.Engine.GetAll().Single(), size);

            rig.Shell.ConfirmResult = false;
            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].DeleteFileCommand).ExecuteAsync());
            Assert.True(File.Exists(path) && await rig.Ui.Run(() => rig.Vm.Downloads.Count) == 1, "declining the confirmation keeps the file and the entry");

            rig.Shell.ConfirmResult = true;
            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads[0].DeleteFileCommand).ExecuteAsync());
            Assert.True(!File.Exists(path) && await rig.Ui.Run(() => rig.Vm.Downloads.Count) == 0, "confirmed delete removes the file and the entry");
        });

        yield return new TestCase("ui: cancel and remove leave no ghost entries and no partial files", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            await using var rig = await VmRig.CreateAsync(env);
            await rig.AddAsync(env.Server.Url("range+slow", 6 * MiB, "one.bin").AbsoluteUri + "\n" + env.Server.Url("range+slow", 6 * MiB, "two.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 2 && vm.Downloads.All(d => d.ProgressPercent > 5), "both started");

            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads.Single(d => d.FileName == "one.bin").CancelCommand).ExecuteAsync());
            await rig.WaitUntilAsync(vm => vm.Downloads.Single(d => d.FileName == "one.bin").Status == DownloadStatus.Cancelled, "Cancelled");
            await rig.Ui.Run(() => Assert.True(rig.Vm.Downloads.Single(d => d.FileName == "one.bin").ShowStart, "a cancelled download can be started again"));

            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.Downloads.Single(d => d.FileName == "two.bin").RemoveCommand).ExecuteAsync());
            await Task.Delay(600, env.Ct);                                   // let any straggling events arrive
            await rig.Ui.Run(() => Assert.Equal(1, rig.Vm.Downloads.Count, "removed item stays removed; cancelled one remains"));
            Assert.True(!Directory.GetFiles(env.Dir).Any(f => f.Contains("two.bin")), "partial file of the removed download is gone");
        });

        yield return new TestCase("ui: drag and drop / paste of links opens the pre-filled dialog", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            await rig.Ui.Run(() =>
            {
                Assert.True(!rig.Vm.HandleDroppedText("just some text") && !rig.Vm.IsAddDialogOpen, "plain text is ignored");
                Assert.True(rig.Vm.HandleDroppedText("https://example.com/a.zip"), "link accepted");
                Assert.True(rig.Vm.IsAddDialogOpen && rig.Vm.AddDialog!.UrlsText == "https://example.com/a.zip", "dialog pre-filled");
                rig.Vm.AddDialog!.CancelCommand.Execute(null);
                Assert.True(!rig.Vm.IsAddDialogOpen, "cancel closes the dialog");
                rig.Vm.OpenAddDialog(null);
                rig.Vm.AddDialog!.UrlsText = "nothing valid";
            });
            await rig.Ui.RunAsync(() => ((AsyncRelayCommand)rig.Vm.AddDialog!.ConfirmCommand).ExecuteAsync());
            await rig.Ui.Run(() => Assert.True(rig.Vm.IsAddDialogOpen && rig.Vm.AddDialog!.Message == "No valid http(s) links were found.", "invalid input keeps the dialog open with a message"));
        });

        yield return new TestCase("ui: settings persist, apply language/theme/RTL, are clamped, and the speed limit reaches the engine", async env =>
        {
            await using var rig = await VmRig.CreateAsync(env);
            await rig.AddAsync(env.Server.Url("range", MiB, "s.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "first download");

            await rig.Ui.Run(() =>
            {
                var s = rig.Vm.SettingsPage;
                s.IsArabic = true; s.IsDarkTheme = true; s.DownloadDirectory = env.Dir;
                s.Connections = 99; s.MaxConcurrentDownloads = 0; s.SpeedLimitKBps = 512;
                s.SaveCommand.Execute(null);
                Assert.True(rig.Vm.IsRtl && rig.Vm.Theme == "dark", "RTL and dark theme applied");
                Assert.Equal("إضافة رابط", rig.Vm.Loc["add_url"], "Arabic text applied everywhere");
                Assert.Equal("مكتمل", rig.Vm.Downloads[0].StatusText, "existing rows are re-localised");
                Assert.Equal("تم حفظ الإعدادات.", s.Message, "confirmation in the new language");
                Assert.True(s.Connections == 32 && s.MaxConcurrentDownloads == 1, $"values clamped: {s.Connections}/{s.MaxConcurrentDownloads}");
            });

            var stored = AppSettings.Load(rig.Settings, "x");
            Assert.True(stored is { Language: "ar", Theme: "dark", DefaultConnections: 32, MaxConcurrentDownloads: 1, GlobalSpeedLimitKBps: 512 }, "persisted in SQLite");

            var sw = Stopwatch.StartNew();
            await rig.AddAsync(env.Server.Url("range", MiB, "limited.bin").AbsoluteUri);
            await rig.WaitUntilAsync(vm => vm.Downloads.Count == 2 && vm.Downloads.All(d => d.Status == DownloadStatus.Completed), "limited download");
            Assert.True(sw.Elapsed.TotalSeconds >= 1.4, $"1 MiB at the saved 512 KB/s limit took only {sw.Elapsed.TotalSeconds:0.00}s");
        });

        yield return new TestCase("ui: restart restores history; an interrupted download comes back paused and finishes", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long big = 6 * MiB;
            var dbPath = Path.Combine(env.Dir, "..", "restart.db");
            await using (var first = await VmRig.CreateAsync(env, dbPath))
            {
                await first.AddAsync(env.Server.Url("range", 200_000, "done.bin").AbsoluteUri);
                await first.WaitUntilAsync(vm => vm.Downloads.Count == 1 && vm.Downloads[0].Status == DownloadStatus.Completed, "small one done");
                await first.AddAsync(env.Server.Url("range+slow", big, "partial.bin").AbsoluteUri);
                await first.WaitUntilAsync(vm => vm.Downloads.Count == 2 && vm.Downloads.Single(d => d.FileName == "partial.bin").ProgressPercent > 20, "partial progress");
            }                                                              // closing the first app pauses what was running

            await using var second = await VmRig.CreateAsync(env, dbPath);
            await second.Ui.Run(() =>
            {
                var vm = second.Vm;
                Assert.Equal(2, vm.Downloads.Count, "history restored");
                var done = vm.Downloads.Single(d => d.FileName == "done.bin");
                var part = vm.Downloads.Single(d => d.FileName == "partial.bin");
                Assert.True(done.Status == DownloadStatus.Completed && done.ProgressPercent == 100, "completed entry intact");
                Assert.True(part.Status == DownloadStatus.Paused && part.ProgressPercent > 0 && part.ProgressPercent < 100, $"interrupted entry is Paused at {part.ProgressPercent:0}%");
            });
            await second.Ui.RunAsync(() => ((AsyncRelayCommand)second.Vm.Downloads.Single(d => d.FileName == "partial.bin").StartCommand).ExecuteAsync());
            await second.WaitUntilAsync(vm => vm.Downloads.Single(d => d.FileName == "partial.bin").Status == DownloadStatus.Completed, "resumed to completion");
            Assert.FileMatches(second.Engine.GetAll().Single(i => i.FileName == "partial.bin"), big);
        });
    }
}
