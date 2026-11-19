using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;

namespace DownloadManager.Presentation.ViewModels;

/// <summary>A sidebar entry (status filter or category) with a live, localised title and a counter.</summary>
public sealed class NavItem : ObservableObject
{
    private readonly Localizer _loc;
    private readonly string _titleKey;
    private int _count;

    public NavItem(string key, string titleKey, string glyph, Localizer loc)
    {
        Key = key;
        Glyph = glyph;
        _titleKey = titleKey;
        _loc = loc;
        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]") OnPropertyChanged(nameof(Title));
        };
    }

    public string Key { get; }
    public string Glyph { get; }
    public string Title => _loc[_titleKey];

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}
