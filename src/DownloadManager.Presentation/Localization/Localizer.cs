using System.Globalization;
using DownloadManager.Presentation.Mvvm;

namespace DownloadManager.Presentation.Localization;

/// <summary>
/// Runtime language switching. Bind with <c>{Binding Loc[key]}</c>; changing <see cref="Language"/> raises
/// "Item[]" so every bound string refreshes without recreating any view.
/// </summary>
public sealed class Localizer : ObservableObject
{
    public const string English = "en";
    public const string Arabic = "ar";

    private string _language = English;

    public string Language
    {
        get => _language;
        set
        {
            var normalized = value == Arabic ? Arabic : English;
            if (normalized == _language) return;
            _language = normalized;
            OnPropertyChanged();
            OnPropertyChanged("Item[]");
            OnPropertyChanged(nameof(IsRtl));
        }
    }

    public bool IsRtl => _language == Arabic;

    public string this[string key] => Lookup(key);

    public string Format(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, Lookup(key), args);

    public string Lookup(string key)
    {
        var table = _language == Arabic ? Strings.Ar : Strings.En;
        if (table.TryGetValue(key, out var text)) return text;
        return Strings.En.TryGetValue(key, out var fallback) ? fallback : key;
    }
}
