using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DownloadManager.Presentation.ViewModels;

namespace DownloadManager.App;

public partial class DownloadWindow : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly DownloadSessionViewModel _vm;
    private bool _lastShowConnectionDetails;
    private bool _lastIsPreDownload;
    private int _lastTabIndex;
    private bool _layoutRefreshQueued;

    public DownloadWindow(DownloadSessionViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        _lastShowConnectionDetails = viewModel.ShowConnectionDetails;
        _lastIsPreDownload = viewModel.IsPreDownload;
        _lastTabIndex = GetTabIndex(viewModel);
        DataContext = viewModel;
        viewModel.RequestClose += Close;
        viewModel.PropertyChanged += OnSessionPropertyChanged;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= Close;
            viewModel.PropertyChanged -= OnSessionPropertyChanged;
        };
    }

    public DownloadSessionViewModel Session => _vm;

    private static int GetTabIndex(DownloadSessionViewModel vm) =>
        vm.IsSpeedLimiterTab ? 1 : (vm.IsCompletionTab ? 2 : 0);

    private void QueueSizeToContentRefresh()
    {
        if (_layoutRefreshQueued) return;
        _layoutRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _layoutRefreshQueued = false;
            InvalidateMeasure();
        }, DispatcherPriority.Loaded);
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var showConn = _vm.ShowConnectionDetails;
        var tabIdx = GetTabIndex(_vm);
        var isPre = _vm.IsPreDownload;

        if (showConn != _lastShowConnectionDetails || tabIdx != _lastTabIndex)
        {
            _lastShowConnectionDetails = showConn;
            _lastTabIndex = tabIdx;
            QueueSizeToContentRefresh();
        }

        if (isPre != _lastIsPreDownload)
        {
            _lastIsPreDownload = isPre;
            QueueSizeToContentRefresh();
            if (!isPre)
            {
                // Once download starts, keep visible on top briefly then release Topmost so user can switch apps freely
                var releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                releaseTimer.Tick += (_, _) =>
                {
                    releaseTimer.Stop();
                    Topmost = false;
                };
                releaseTimer.Start();
            }
        }
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Show();
        Activate();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);

        // Keep Pre-Download prompt Topmost so browser never covers it; for Progress windows release Topmost after 800ms
        if (!_vm.IsPreDownload)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Topmost = false;
            };
            timer.Start();
        }
    }
}
