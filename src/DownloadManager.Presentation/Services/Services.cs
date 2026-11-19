namespace DownloadManager.Presentation.Services;

/// <summary>Marshals work onto the UI thread. The WPF app implements it with Dispatcher.BeginInvoke.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Operating-system interactions the view-models need but must not implement themselves.</summary>
public interface IShellService
{
    void OpenFile(string path);
    void OpenFolder(string path);
    string? PickFolder(string? initialDirectory);
    bool Confirm(string message);
}
