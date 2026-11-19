using System.Windows.Input;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;
using DownloadManager.Presentation.Settings;

namespace DownloadManager.Presentation.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/>; nothing is applied until Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private string _language = Localizer.English;
    private string _theme = AppSettings.Light;
    private string _directory = "";
    private int _connections = 8;
    private int _maxConcurrent = 3;
    private long _speedLimit;
    private int _autoResume = 0;
    private int _monitorClipboard = 0;
    private int _minimizeToTray = 1;
    private int _closeToTray = 1;
    private int _showNotifications = 1;
    private int _showDownloadWindow = 1;
    private int _startWithWindows = 1;
    private int _defaultPriority = 50;
    private string _message = "";

    public event Action<AppSettings>? SettingsSaved;

    public SettingsViewModel(Action<SettingsViewModel> save, Func<string?> browse)
    {
        SaveCommand = new RelayCommand(() =>
        {
            save(this);
            var model = ToModel();
            SettingsSaved?.Invoke(model);
        });
        BrowseCommand = new RelayCommand(() =>
        {
            var picked = browse();
            if (!string.IsNullOrWhiteSpace(picked)) DownloadDirectory = picked;
        });
    }

    public string Language { get => _language; set { if (Set(ref _language, value)) { OnPropertyChanged(nameof(IsEnglish)); OnPropertyChanged(nameof(IsArabic)); } } }
    public string Theme { get => _theme; set { if (Set(ref _theme, value)) { OnPropertyChanged(nameof(IsLightTheme)); OnPropertyChanged(nameof(IsDarkTheme)); } } }
    public string DownloadDirectory { get => _directory; set => Set(ref _directory, value); }
    public int Connections { get => _connections; set => Set(ref _connections, value); }
    public int MaxConcurrentDownloads { get => _maxConcurrent; set => Set(ref _maxConcurrent, value); }
    public long SpeedLimitKBps { get => _speedLimit; set => Set(ref _speedLimit, value); }
    public int AutoResumeDownloads { get => _autoResume; set { if (Set(ref _autoResume, value)) OnPropertyChanged(nameof(AutoResumeBool)); } }
    public int MonitorClipboard { get => _monitorClipboard; set { if (Set(ref _monitorClipboard, value)) OnPropertyChanged(nameof(MonitorClipboardBool)); } }
    public int MinimizeToTray { get => _minimizeToTray; set { if (Set(ref _minimizeToTray, value)) OnPropertyChanged(nameof(MinimizeToTrayBool)); } }
    public int CloseToTray { get => _closeToTray; set { if (Set(ref _closeToTray, value)) OnPropertyChanged(nameof(CloseToTrayBool)); } }
    public int ShowNotifications { get => _showNotifications; set { if (Set(ref _showNotifications, value)) OnPropertyChanged(nameof(ShowNotificationsBool)); } }
    public int ShowDownloadWindow { get => _showDownloadWindow; set { if (Set(ref _showDownloadWindow, value)) OnPropertyChanged(nameof(ShowDownloadWindowBool)); } }
    public int StartWithWindows { get => _startWithWindows; set { if (Set(ref _startWithWindows, value)) OnPropertyChanged(nameof(StartWithWindowsBool)); } }
    public int DefaultPriority { get => _defaultPriority; set => Set(ref _defaultPriority, value); }
    public string Message { get => _message; set => Set(ref _message, value); }

    // Two-way bool properties for RadioButtons and Toggle Switches (CheckBox).
    public bool IsEnglish { get => _language == Localizer.English; set { if (value) Language = Localizer.English; } }
    public bool IsArabic { get => _language == Localizer.Arabic; set { if (value) Language = Localizer.Arabic; } }
    public bool IsLightTheme { get => _theme == AppSettings.Light; set { if (value) Theme = AppSettings.Light; } }
    public bool IsDarkTheme { get => _theme == AppSettings.Dark; set { if (value) Theme = AppSettings.Dark; } }

    public bool AutoResumeBool { get => _autoResume != 0; set => AutoResumeDownloads = value ? 1 : 0; }
    public bool MonitorClipboardBool { get => _monitorClipboard != 0; set => MonitorClipboard = value ? 1 : 0; }
    public bool MinimizeToTrayBool { get => _minimizeToTray != 0; set => MinimizeToTray = value ? 1 : 0; }
    public bool CloseToTrayBool { get => _closeToTray != 0; set => CloseToTray = value ? 1 : 0; }
    public bool ShowNotificationsBool { get => _showNotifications != 0; set => ShowNotifications = value ? 1 : 0; }
    public bool ShowDownloadWindowBool { get => _showDownloadWindow != 0; set => ShowDownloadWindow = value ? 1 : 0; }
    public bool StartWithWindowsBool { get => _startWithWindows != 0; set => StartWithWindows = value ? 1 : 0; }

    public ICommand SaveCommand { get; }
    public ICommand BrowseCommand { get; }

    public void Load(AppSettings s)
    {
        Language = s.Language;
        Theme = s.Theme;
        DownloadDirectory = s.DownloadDirectory;
        Connections = s.DefaultConnections;
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        SpeedLimitKBps = s.GlobalSpeedLimitKBps;
        AutoResumeDownloads = s.AutoResumeDownloads;
        MonitorClipboard = s.MonitorClipboard;
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray = s.CloseToTray;
        ShowNotifications = s.ShowNotifications;
        ShowDownloadWindow = s.ShowDownloadWindow;
        StartWithWindows = s.StartWithWindows;
        DefaultPriority = s.DefaultPriority;
    }

    public AppSettings ToModel() => new()
    {
        Language = Language,
        Theme = Theme,
        DownloadDirectory = DownloadDirectory,
        DefaultConnections = Connections,
        MaxConcurrentDownloads = MaxConcurrentDownloads,
        GlobalSpeedLimitKBps = SpeedLimitKBps,
        AutoResumeDownloads = AutoResumeDownloads,
        MonitorClipboard = MonitorClipboard,
        MinimizeToTray = MinimizeToTray,
        CloseToTray = CloseToTray,
        ShowNotifications = ShowNotifications,
        ShowDownloadWindow = ShowDownloadWindow,
        StartWithWindows = StartWithWindows,
        DefaultPriority = DefaultPriority,
    };
}
