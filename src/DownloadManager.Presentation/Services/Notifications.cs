namespace DownloadManager.Presentation.Services;

/// <summary>Shows a short user-visible notification. Implemented by the WPF app with tray balloon tips.</summary>
public interface INotificationService
{
    void Show(string title, string message, bool isError = false);
}