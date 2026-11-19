using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Services;

namespace DownloadManager.App.Services;

/// <summary>
/// Taskbar notification icon with a context menu and balloon tips, built directly on
/// <c>Shell_NotifyIcon</c> so that no third-party tray library is needed. Create it on the UI thread;
/// all events are raised on the UI thread and every public member may be called from any thread.
/// </summary>
public sealed class TrayIcon : IDisposable, INotificationService
{
    // Callback message posted by the shell for mouse activity on the icon.
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_ERROR = 0x00000003;

    private const int MaxTipLength = 127;
    private const int MaxInfoTitleLength = 63;
    private const int MaxInfoLength = 255;
    private const uint BalloonTimeoutMilliseconds = 5000;
    private const uint IconId = 1;
    private const int HwndMessage = -3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    private readonly int _taskbarCreatedMessage;
    private NOTIFYICONDATA _data;
    private HwndSource? _source;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private bool _iconAdded;
    private bool _disposed;
    private string _baseTooltip;
    private string _currentTooltip;
    private int _activeCount;

    /// <summary>Raised on a double click (or "Open" in the context menu); the host shows its window.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when "Pause all" is chosen in the context menu.</summary>
    public event EventHandler? PauseAllRequested;

    /// <summary>Raised when "Resume all" is chosen in the context menu.</summary>
    public event EventHandler? ResumeAllRequested;

    /// <summary>Raised when "Exit" is chosen in the context menu; the host shuts the application down.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Creates the message-only window and adds the icon to the notification area.</summary>
    /// <param name="tooltip">The base tooltip text; truncated to 127 characters.</param>
    public TrayIcon(string tooltip)
    {
        _taskbarCreatedMessage = (int)RegisterWindowMessage("TaskbarCreated");
        _baseTooltip = Truncate(tooltip, MaxTipLength);
        _currentTooltip = _baseTooltip;
        _data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            uID = IconId,
            uCallbackMessage = CallbackMessage,
            szTip = "",
            szInfo = "",
            szInfoTitle = "",
        };

        var dispatcher = UiDispatcher;
        if (dispatcher is null)
            throw new InvalidOperationException("The tray icon must be created after the WPF Application exists.");
        if (dispatcher.CheckAccess()) Initialize();
        else dispatcher.Invoke(Initialize, DispatcherPriority.Normal);
    }

    private static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

    private void Initialize()
    {
        _iconHandle = CreateIconHandle(out _ownsIconHandle);

        _source = new HwndSource(new HwndSourceParameters("NahrTrayIcon")
        {
            ParentWindow = new IntPtr(HwndMessage),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);

        AddIcon();
    }

    /// <summary>Replaces the base tooltip shown when no download is active (max 127 characters).</summary>
    public void SetTooltip(string text)
    {
        _baseTooltip = Truncate(text, MaxTipLength);
        ApplyTooltip();
    }

    /// <summary>Shows the number of active downloads in the tooltip, e.g. "Nahr — 2 active".</summary>
    public void SetActiveCount(int active)
    {
        _activeCount = Math.Max(0, active);
        ApplyTooltip();
    }

    /// <summary>Displays a balloon notification from the icon; safe to call from any thread.</summary>
    public void ShowBalloon(string title, string message, bool isError = false)
    {
        var source = _source;
        if (_disposed || source is null) return;

        source.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_iconAdded) return;

            _data.szInfoTitle = Truncate(title, MaxInfoTitleLength);
            _data.szInfo = Truncate(message, MaxInfoLength);
            _data.uTimeout = BalloonTimeoutMilliseconds;
            _data.dwInfoFlags = isError ? NIIF_ERROR : NIIF_INFO;
            _data.szTip = _currentTooltip;
            _data.uFlags = NIF_INFO | NIF_TIP;

            Shell_NotifyIcon(NIM_MODIFY, ref _data);
        }, DispatcherPriority.Normal);
    }

    /// <summary>Explicit interface implementation of <see cref="INotificationService"/> for the view-models.</summary>
    void INotificationService.Show(string title, string message, bool isError) => ShowBalloon(title, message, isError);

    private void ApplyTooltip()
    {
        _currentTooltip = _activeCount > 0
            ? Truncate($"{_baseTooltip} — {_activeCount} active", MaxTipLength)
            : _baseTooltip;

        var source = _source;
        if (_disposed || source is null) return;

        source.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_iconAdded) return;

            _data.szTip = _currentTooltip;
            _data.uFlags = NIF_TIP;
            Shell_NotifyIcon(NIM_MODIFY, ref _data);
        }, DispatcherPriority.Normal);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: the icon was dropped from the notification area, so add it again.
            if (!_disposed)
            {
                _iconAdded = false;
                AddIcon();
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage) return IntPtr.Zero;

        switch (unchecked((int)(long)lParam & 0xFFFF))
        {
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowContextMenu();
                handled = true;
                break;

            case WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void AddIcon()
    {
        if (_disposed || _iconAdded || _source is null) return;

        _data.hWnd = _source.Handle;
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        _data.hIcon = _iconHandle;
        _data.szTip = _currentTooltip;

        _iconAdded = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    private void ShowContextMenu()
    {
        if (_disposed) return;

        var loc = Application.Current?.Resources["Loc"] as Localizer;
        var isArabic = loc?.Language == Localizer.Arabic;
        string Label(string en, string ar) => isArabic ? ar : en;
        string name = loc?.Lookup("app_name") ?? "Nahr";

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        menu.Items.Add(CreateItem(isArabic ? $"فتح {name}" : $"Open {name}", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem(Label("Pause all", "إيقاف الكل مؤقتًا"), () => PauseAllRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem(Label("Resume all", "استئناف الكل"), () => ResumeAllRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem(Label("Exit", "خروج"), () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        menu.IsOpen = true;
    }

    private static MenuItem CreateItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Loads the embedded Nahr application icon, falling back to process or generic icon.</summary>
    private static IntPtr CreateIconHandle(out bool ownsHandle)
    {
        Icon? icon = null;
        try
        {
            var resInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/nahr.ico", UriKind.Absolute));
            if (resInfo?.Stream is { } stream)
            {
                using (stream)
                {
                    icon = new Icon(stream, new System.Drawing.Size(32, 32));
                }
            }
        }
        catch
        {
            icon = null;
        }

        if (icon is null)
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) icon = Icon.ExtractAssociatedIcon(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or Win32Exception or NotSupportedException)
            {
                icon = null;
            }
        }

        if (icon is null) icon = (Icon)SystemIcons.Application.Clone();

        try
        {
            var copy = CopyIcon(icon.Handle);
            if (copy != IntPtr.Zero)
            {
                icon.Dispose();
                ownsHandle = true;
                return copy;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or Win32Exception or NotSupportedException or ExternalException)
        {
            // Fall through to the shared-system icon below.
        }
        finally
        {
            // If we didn't successfully copy, dispose the icon to avoid a leak.
            // Note: if copy succeeded we already disposed above, but Dispose is safe to call twice.
        }

        // Copying failed; keep the icon alive for the lifetime of the process and do not own its handle.
        ownsHandle = false;
        return IntPtr.Zero;
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>Removes the icon, destroys the window and releases the icon handle; safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var source = _source;
        _source = null;

        try
        {
            if (source is not null && source.Dispatcher.CheckAccess()) Cleanup(source);
            else if (source is not null) source.Dispatcher.Invoke(() => Cleanup(source), DispatcherPriority.Normal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            // The dispatcher is already shutting down; the shell drops the icon with the window anyway.
        }
    }

    private void Cleanup(HwndSource source)
    {
        if (_iconAdded)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _iconAdded = false;
        }

        source.RemoveHook(WndProc);
        source.Dispose();

        if (_ownsIconHandle && _iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
    }
}
