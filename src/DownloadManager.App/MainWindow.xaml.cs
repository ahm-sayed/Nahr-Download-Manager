using System.Windows;
using DownloadManager.Presentation.ViewModels;

namespace DownloadManager.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // ------------------------------------------------------------------ drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetText(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>Dropping a link (or text containing links) from a browser opens the Add dialog pre-filled.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm && TryGetText(e) is { } text) vm.HandleDroppedText(text);
        e.Handled = true;
    }

    private static string? TryGetText(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.UnicodeText)) return e.Data.GetData(DataFormats.UnicodeText) as string;
        if (e.Data.GetDataPresent(DataFormats.Text)) return e.Data.GetData(DataFormats.Text) as string;
        return null;
    }
}
