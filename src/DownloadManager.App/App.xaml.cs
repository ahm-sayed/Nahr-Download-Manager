using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DownloadManager.App.Services;
using DownloadManager.Data;
using DownloadManager.Engine;
using DownloadManager.Infrastructure.Logging;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;
using DownloadManager.Presentation.Services;
using DownloadManager.Presentation.Settings;
using DownloadManager.Presentation.ViewModels;
using Microsoft.Extensions.Logging;

namespace DownloadManager.App;

/// <summary>Composition root: wires SQLite, logging, the engine and the view-model, then shows the window.</summary>
public partial class App : Application
{
    private const int AsfwAny = -1;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int DwmwaCloaked = 14;
    private const int UoiName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private readonly List<DownloadWindow> _downloadWindows = new();
    private FloatingTrackerWindow? _floatingTracker;
    private Mutex? _singleInstanceMutex;
    private SqliteDatabase? _db;
    private DownloadEngine? _engine;
    private MainViewModel? _viewModel;
    private ILoggerFactory? _loggers;
    private ILogger? _log;
    private TrayIcon? _tray;
    private ClipboardMonitor? _clipboardMonitor;
    private MainWindow? _window;
    private AppSettings? _settings;
    private ExtensionServer? _extensionServer;
    private System.Windows.Threading.DispatcherTimer? _topmostTimer;
    private bool _restorePending;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Fast path: Chrome / Edge / Brave / Firefox launched Nahr as a Native Messaging Host.
        // Speak the 4-byte length-prefixed JSON stdio protocol, start Nahr silently in tray (--minimized),
        // wait for the extension HTTP server, reply, and exit without touching WPF windows or SQLite.
        if (WindowsIntegration.IsNativeMessagingHostLaunch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await WindowsIntegration.RunNativeMessagingHostAsync(e.Args);
            Shutdown(0);
            return;
        }

        // Use GPU-accelerated WPF rendering by default for smooth 60+ FPS progress bars and animations;
        // pass --software if a specific GPU driver requires CPU software rendering.
        if (e.Args.Any(a => string.Equals(a, "--software", StringComparison.OrdinalIgnoreCase)))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var startMinimized = e.Args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));

        base.OnStartup(e);

        // Ensure only a single Nahr instance runs at a time; if already running in tray, grant foreground
        // permission and bring the running MainWindow straight to the front (unless launched --minimized).
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\NahrDownloadManager_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;

            if (startMinimized)
            {
                Shutdown(0);
                return;
            }

            // Grant foreground activation permission before notifying the tray instance.
            try { AllowSetForegroundWindow(AsfwAny); } catch { }

            var myDesktop = GetCurrentDesktopName();
            var shownOnSameDesktop = false;
            int? wrongDesktopPid = null;
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{ExtensionServer.DefaultPort}/show");
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        string? otherDesktop = null;
                        int? otherPid = null;
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("desktop", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                otherDesktop = dEl.GetString();
                            if (doc.RootElement.TryGetProperty("pid", out var pEl) && pEl.TryGetInt32(out var pidVal))
                                otherPid = pidVal;
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(otherDesktop)
                            && !string.Equals(otherDesktop, myDesktop, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(myDesktop, "Default", StringComparison.OrdinalIgnoreCase))
                        {
                            // Existing instance is trapped on a hidden/isolated desktop (e.g. an IDE sandbox terminal)
                            // while the user launched Nahr on the visible Default desktop: replace the hidden instance.
                            wrongDesktopPid = otherPid;
                            break;
                        }

                        shownOnSameDesktop = true;
                        break;
                    }
                }
                catch { }
                await Task.Delay(250);
            }

            if (shownOnSameDesktop)
            {
                Shutdown(0);
                return;
            }

            if (wrongDesktopPid is { } stalePid && stalePid != Environment.ProcessId)
            {
                try
                {
                    using var stale = Process.GetProcessById(stalePid);
                    if (string.Equals(stale.ProcessName, "Nahr", StringComparison.OrdinalIgnoreCase))
                    {
                        stale.Kill(entireProcessTree: true);
                        stale.WaitForExit(2000);
                    }
                }
                catch { }
            }

            // Fallback: if the existing instance isn't responding on HTTP, try restoring its window directly
            // or terminate only an unresponsive zombie Nahr process so this launch can open cleanly.
            var recovered = false;
            var currentPid = Environment.ProcessId;
            if (wrongDesktopPid is null)
            {
                foreach (var proc in Process.GetProcessesByName("Nahr"))
                {
                    if (proc.Id == currentPid) continue;
                    try
                    {
                        proc.Refresh();
                        if (proc.Responding && proc.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(proc.MainWindowHandle, IsIconic(proc.MainWindowHandle) ? SwRestore : SwShow);
                            BringWindowToTop(proc.MainWindowHandle);
                            SetForegroundWindow(proc.MainWindowHandle);
                            recovered = true;
                            break;
                        }
                        if (!proc.Responding)
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(1500);
                        }
                    }
                    catch { }
                }
            }

            if (recovered)
            {
                Shutdown(0);
                return;
            }

            // Retry acquiring the single-instance mutex if a hidden-desktop or hung process was cleaned up
            _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\NahrDownloadManager_SingleInstance", out createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown(0);
                return;
            }
        }

        // The main window may be hidden in the tray while standalone download windows operate independently.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            _log?.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(args.Exception.Message, "Nahr", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _log?.LogCritical(ex, "Unhandled AppDomain exception (terminating={Terminating})", args.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nahr");
            Directory.CreateDirectory(dataDirectory);

            _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information)
                .AddProvider(new FileLoggerProvider(Path.Combine(dataDirectory, "logs"))));
            _log = _loggers.CreateLogger("Nahr.App");
            _log.LogInformation(
                "Startup pid={Pid} desktop={Desktop} renderMode={RenderMode} tier={Tier} workArea={WorkArea} exe={Exe}",
                Environment.ProcessId,
                GetCurrentDesktopName(),
                RenderOptions.ProcessRenderMode,
                RenderCapability.Tier >> 16,
                SystemParameters.WorkArea,
                Environment.ProcessPath);

            _db = SqliteDatabase.Open(Path.Combine(dataDirectory, "nahr.db"));
            var settingsStore = new SqliteSettingsStore(_db);
            var defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            _settings = AppSettings.Load(settingsStore, defaultDirectory);

            _engine = new DownloadEngine(
                new SqliteDownloadStore(_db),
                new EngineOptions
                {
                    DefaultConnections = _settings.DefaultConnections,
                    MaxConcurrentDownloads = _settings.MaxConcurrentDownloads,
                    GlobalSpeedLimitBytesPerSecond = _settings.GlobalSpeedLimitKBps * 1024,
                },
                logger: _loggers.CreateLogger<DownloadEngine>());

            var loc = new Localizer { Language = _settings.Language };
            Resources["Loc"] = loc;                                   // must exist before MainWindow is created

            AsyncRelayCommand.Error = ex =>
            {
                _log.LogError(ex, "A command failed");
                MessageBox.Show(loc.Format("error_generic", ex.Message), "Nahr", MessageBoxButton.OK, MessageBoxImage.Warning);
            };

            _viewModel = new MainViewModel(_engine, settingsStore, _settings, loc, new WpfDispatcher(Dispatcher), new WindowsShell());
            ApplyTheme(_viewModel.Theme);
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.Theme)) ApplyTheme(_viewModel.Theme);
                else if (args.PropertyName == nameof(MainViewModel.HasFloatingTracker))
                    _floatingTracker?.SyncVisibility(_viewModel.HasFloatingTracker);
                else if (args.PropertyName == nameof(MainViewModel.ActiveCount))
                    _tray?.SetActiveCount(_viewModel.ActiveCount);
            };
            _viewModel.DownloadWindowRequested += (_, session) => ShowDownloadWindow(session);

            CreateTray(loc);
            _viewModel.SettingsChanged += (_, model) => ApplyBehaviourSettings(loc, model);

            await _viewModel.InitializeAsync();

            // Create and assign MainWindow BEFORE any secondary/floating windows so it is always the primary window.
            _window = new MainWindow { DataContext = _viewModel };
            MainWindow = _window;
            _window.Closing += OnWindowClosing;
            _window.StateChanged += OnWindowStateChanged;
            _window.IsVisibleChanged += (_, _) => LogWindowState("IsVisibleChanged");
            _window.Loaded += (_, _) => LogWindowState("Loaded");
            _window.ContentRendered += (_, _) => LogWindowState("ContentRendered");

            if (!startMinimized)
            {
                RestoreWindow();
                if (_restorePending)
                {
                    _restorePending = false;
                    RestoreWindow();
                }
            }
            else
            {
                _log?.LogInformation("Started silently in background tray (--minimized)");
            }

            _floatingTracker = new FloatingTrackerWindow(_viewModel);
            _floatingTracker.SyncVisibility(_viewModel.HasFloatingTracker);

            ApplyBehaviourSettings(loc, _settings);

            // Start the extension server: opens ONLY the standalone Download Window (never pops up MainWindow!).
            try
            {
                _extensionServer = new ExtensionServer(ExtensionServer.DefaultPort, _loggers.CreateLogger<ExtensionServer>())
                {
                    DesktopName = GetCurrentDesktopName()
                };
                _extensionServer.ExtensionOriginObserved += (_, origin) =>
                    WindowsIntegration.RegisterNativeMessagingHost(Environment.ProcessPath, [origin]);
                _extensionServer.ShowMainWindowRequested += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    _log?.LogInformation("Received /show request; restoring MainWindow");
                    RestoreWindow();
                });
                _extensionServer.DownloadRequested += (_, req) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            _viewModel?.OpenStandaloneDownloadPrompt(req);
                        }
                        catch (Exception ex)
                        {
                            _log?.LogError(ex, "Failed to open download prompt from extension");
                        }
                    });
                };
                if (_viewModel is not null)
                {
                    _viewModel.BrowserFallbackRequested += (_, fb) =>
                        _extensionServer?.QueueBrowserFallback(fb.Url, fb.FileName);
                }
                _extensionServer.Start();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Could not start extension server on port {Port}", ExtensionServer.DefaultPort);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Startup failed");
            MessageBox.Show("Nahr could not start:\n" + ex.Message, "Nahr", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // ------------------------------------------------------------------ standalone download windows

    private void ShowDownloadWindow(DownloadSessionViewModel session)
    {
        if (session.BoundDownloadId is { } existingId)
        {
            var existing = _downloadWindows.FirstOrDefault(w => w.Session.BoundDownloadId == existingId);
            if (existing is not null)
            {
                existing.BringToFront();
                return;
            }
        }

        var win = new DownloadWindow(session);
        _downloadWindows.Add(win);
        win.Closed += (_, _) => _downloadWindows.Remove(win);
        win.BringToFront();
    }

    // ------------------------------------------------------------------ tray and clipboard

    private void CreateTray(Localizer loc)
    {
        _tray = new TrayIcon(loc.Lookup("app_name"));
        _tray.OpenRequested += (_, _) => Dispatcher.BeginInvoke(RestoreWindow);
        _tray.PauseAllRequested += async (_, _) => { try { if (_viewModel is not null) await _viewModel.PauseAllAsync(); } catch (Exception ex) { _log?.LogError(ex, "PauseAll from tray failed"); } };
        _tray.ResumeAllRequested += async (_, _) => { try { if (_viewModel is not null) await _viewModel.ResumeAllAsync(); } catch (Exception ex) { _log?.LogError(ex, "ResumeAll from tray failed"); } };
        _tray.ExitRequested += (_, _) =>
        {
            _exiting = true;
            Shutdown(0);
        };
    }

    private void RestoreWindow()
    {
        if (_window is null)
        {
            _restorePending = true;
            return;
        }

        _window.ShowInTaskbar = true;
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        // Guard against off-screen coordinates or monitors smaller than default size (e.g. 150% laptop scaling)
        var work = SystemParameters.WorkArea;
        if (_window.ActualWidth > work.Width || _window.Width > work.Width)
            _window.Width = Math.Max(_window.MinWidth, work.Width - 32);
        if (_window.ActualHeight > work.Height || _window.Height > work.Height)
            _window.Height = Math.Max(_window.MinHeight, work.Height - 24);
        if (double.IsNaN(_window.Left) || _window.Left < work.Left - 80 || _window.Left > work.Right - 100)
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = work.Left + Math.Max(0, (work.Width - _window.Width) / 2);
        }
        if (double.IsNaN(_window.Top) || _window.Top < work.Top - 40 || _window.Top > work.Bottom - 80)
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Top = work.Top + Math.Max(0, (work.Height - _window.Height) / 2);
        }

        if (!_window.IsVisible)
            _window.Show();

        ForceBringMainWindowToFront();
        LogWindowState("RestoreWindow");
    }

    private void ForceBringMainWindowToFront()
    {
        if (_window is null) return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var fgHwnd = GetForegroundWindow();
        uint fgThread = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out _) : 0;
        uint curThread = GetCurrentThreadId();
        bool attached = false;
        bool setFgOk = false;

        try
        {
            if (fgThread != 0 && fgThread != curThread)
                attached = AttachThreadInput(curThread, fgThread, true);

            ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
            BringWindowToTop(hwnd);
            setFgOk = SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(curThread, fgThread, false);
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Focus();

        _log?.LogInformation("BringToFront hwnd=0x{Hwnd:X} fgBefore=0x{Fg:X} attached={Attached} setFgOk={SetFgOk}",
            hwnd.ToInt64(), fgHwnd.ToInt64(), attached, setFgOk);

        // Hold Topmost briefly across a render frame so Windows commits Z-order above all open windows, then release it.
        if (_topmostTimer is null)
        {
            _topmostTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _topmostTimer.Tick += (_, _) =>
            {
                _topmostTimer.Stop();
                if (_window is not null)
                    _window.Topmost = false;
            };
        }
        _topmostTimer.Stop();
        _topmostTimer.Start();
    }

    private void LogWindowState(string source)
    {
        if (_log is null || _window is null) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        var visWin32 = hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
        var iconic = hwnd != IntPtr.Zero && IsIconic(hwnd);
        var rectStr = "none";
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
            rectStr = $"({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top})";
        var cloaked = -1;
        if (hwnd != IntPtr.Zero)
            _ = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int));

        _log.LogInformation(
            "MainWindow[{Source}] hwnd=0x{Hwnd:X} wpfVis={WpfVis} win32Vis={Win32Vis} iconic={Iconic} cloaked={Cloaked} state={State} actual={Aw:0}x{Ah:0} rect={Rect} active={Active}",
            source, hwnd.ToInt64(), _window.IsVisible, visWin32, iconic, cloaked, _window.WindowState,
            _window.ActualWidth, _window.ActualHeight, rectStr, _window.IsActive);
    }

    private static string GetCurrentDesktopName()
    {
        try
        {
            var desk = GetThreadDesktop(GetCurrentThreadId());
            if (desk == IntPtr.Zero) return "(none)";
            var sb = new StringBuilder(256);
            return GetUserObjectInformation(desk, UoiName, sb, sb.Capacity * 2, out _) ? sb.ToString() : "(err)";
        }
        catch
        {
            return "(err)";
        }
    }

    /// <summary>Applies the settings that affect the shell: clipboard watching, notifications and tray behaviour.</summary>
    private void ApplyBehaviourSettings(Localizer loc, AppSettings model)
    {
        _settings = model;

        var wanted = model.MonitorClipboard != 0;
        if (wanted && _clipboardMonitor is null)
        {
            _clipboardMonitor = new ClipboardMonitor();
            _clipboardMonitor.UrlDetected += (_, urls) =>
            {
                if (_viewModel is null || urls.Count == 0) return;
                if (urls.Count == 1)
                {
                    _viewModel.OpenStandaloneDownloadPrompt(new ExtensionDownloadRequest { Url = urls[0] });
                }
                else
                {
                    var text = string.Join("\n", urls);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _viewModel.HandleDroppedText(text);
                        RestoreWindow();
                    }
                }
            };
        }
        else if (!wanted && _clipboardMonitor is not null)
        {
            _clipboardMonitor.Dispose();
            _clipboardMonitor = null;
        }
        _clipboardMonitor?.Start();

        if (_viewModel is not null)
        {
            _viewModel.NotificationsEnabled = model.ShowNotifications != 0;
            _viewModel.Notifications = _tray;
        }

        // Sync Windows sign-in background startup & browser Native Messaging Host (HKCU only).
        WindowsIntegration.SyncStartWithWindows(model.StartWithWindows != 0, Environment.ProcessPath);
        WindowsIntegration.RegisterNativeMessagingHost(Environment.ProcessPath);
    }

    // ------------------------------------------------------------------ window behaviour

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        LogWindowState("StateChanged");
        if (_settings?.MinimizeToTray != 0 && _window?.WindowState == WindowState.Minimized) _window.Hide();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _log?.LogInformation("MainWindow Closing (exiting={Exiting}, closeToTray={CloseToTray})", _exiting, _settings?.CloseToTray);
        if (_exiting) return;

        if (_settings?.CloseToTray != 0)
        {
            e.Cancel = true;
            _window?.Hide();
            return;
        }

        _exiting = true;
        Shutdown(0);
    }

    /// <summary>Index 0 of the merged dictionaries is the theme; swapping it re-colours every DynamicResource.</summary>
    private void ApplyTheme(string theme)
    {
        var name = theme == AppSettings.Dark ? "Dark" : "Light";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{name}.xaml", UriKind.Relative) };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _exiting = true;

            try { _floatingTracker?.Close(); } catch { }
            _floatingTracker = null;

            foreach (var win in _downloadWindows.ToList())
            {
                try { win.Close(); } catch { }
            }
            _downloadWindows.Clear();

            _clipboardMonitor?.Dispose();
            _clipboardMonitor = null;

            _tray?.Dispose();
            _tray = null;

            Task.Run(async () =>
            {
                if (_extensionServer is not null) await _extensionServer.DisposeAsync();
            }).GetAwaiter().GetResult();

            Task.Run(async () =>
            {
                if (_viewModel is not null) await _viewModel.DisposeAsync();
                if (_engine is not null) await _engine.DisposeAsync();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Shutdown failed");
        }
        _db?.Dispose();
        _loggers?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
