using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DownloadManager.Presentation.Services;

namespace DownloadManager.App.Services;

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}

public sealed class WindowsShell : IShellService
{
    // Opening a downloaded program or script is the classic way to get infected, so ask first.
    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".hta", ".lnk", ".reg", ".msix", ".appx",
    };

    public void OpenFile(string path)
    {
        if (RiskyExtensions.Contains(Path.GetExtension(path)) &&
            !Confirm($"This file can run code on your computer:\n{Path.GetFileName(path)}\n\nOnly open it if you trust where it came from. Continue?\n\n" +
                     "هذا الملف قادر على تشغيل برامج على جهازك. افتحه فقط إذا كنت تثق في مصدره. هل تريد المتابعة؟"))
            return;

        TryStart(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (File.Exists(path)) TryStart(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else TryStart(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public string? PickFolder(string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(message, "Nahr", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static void TryStart(ProcessStartInfo info)
    {
        try { Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(ex.Message, "Nahr", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
