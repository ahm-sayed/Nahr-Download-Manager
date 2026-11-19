using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DownloadManager.Presentation.Services;

namespace DownloadManager.App.Services;

/// <summary>Watches the Windows clipboard and raises <see cref="UrlDetected"/> when new text contains URLs.</summary>
/// <remarks>
/// Must be created and disposed on the WPF UI thread. The listener is a hidden message-only window, so
/// notifications arrive through that thread's message pump; this is also why <see cref="UrlDetected"/> is
/// always raised on the UI thread.
/// </remarks>
public sealed class ClipboardMonitor : IDisposable
{
    private const int HwndMessage = -3;
    private const int WmClipboardUpdate = 0x031D;

    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly IntPtr _hwnd;
    private string? _lastText;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the hidden window that receives clipboard notifications.</summary>
    public ClipboardMonitor()
    {
        var parameters = new HwndSourceParameters("NahrClipboardMonitor")
        {
            ParentWindow = new IntPtr(HwndMessage),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _hwnd = _source.Handle;
        _hook = WndProc;
        _source.AddHook(_hook);
    }

    /// <summary>Raised on the UI thread with the absolute URLs found in newly copied clipboard text.</summary>
    public event EventHandler<IReadOnlyList<string>>? UrlDetected;

    /// <summary>Starts listening for clipboard updates. Calling it again while started does nothing.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        // A restart should treat the next copy as new even if it repeats what was copied before.
        _lastText = null;
        _started = AddClipboardFormatListener(_hwnd);
    }

    /// <summary>Stops listening for clipboard updates. Calling it again while stopped does nothing.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        RemoveClipboardFormatListener(_hwnd);
    }

    /// <summary>Stops listening, releases the window hook and destroys the hidden window.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _source.RemoveHook(_hook);
        _source.Dispose(); // Destroys the hidden window as well.
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmClipboardUpdate) return IntPtr.Zero;

        handled = true;
        ReadClipboard();
        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (ExternalException)
        {
            // Another process holds the clipboard open (or it is in an unusable format); the next
            // WM_CLIPBOARDUPDATE will try again, so there is nothing useful to report here.
            return;
        }

        if (string.IsNullOrEmpty(text)) return;
        if (string.Equals(text, _lastText, StringComparison.Ordinal)) return; // Same content, ignore.
        _lastText = text;

        var urls = UrlExtractor.ExtractUrls(text);
        if (urls.Count == 0) return;

        UrlDetected?.Invoke(this, urls);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
