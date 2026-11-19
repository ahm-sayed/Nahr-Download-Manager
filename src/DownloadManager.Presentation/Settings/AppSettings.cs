using System.Globalization;
using DownloadManager.Core.Abstractions;
using DownloadManager.Engine;
using DownloadManager.Presentation.Localization;

namespace DownloadManager.Presentation.Settings;

public sealed class AppSettings
{
    public const string Light = "light";
    public const string Dark = "dark";

    public string Language { get; set; } = Localizer.English;
    public string Theme { get; set; } = Light;
    public string DownloadDirectory { get; set; } = "";
    public int DefaultConnections { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public long GlobalSpeedLimitKBps { get; set; }
    public int AutoResumeDownloads { get; set; } = 1;
    public int MonitorClipboard { get; set; } = 0;
    public int MinimizeToTray { get; set; } = 1;
    public int CloseToTray { get; set; } = 1;
    public int ShowNotifications { get; set; } = 1;
    public int ShowDownloadWindow { get; set; } = 1;
    public int StartWithWindows { get; set; } = 1;
    public int DefaultPriority { get; set; } = 50;

    public static AppSettings Load(ISettingsStore store, string defaultDirectory)
    {
        var s = new AppSettings
        {
            Language = store.Get("language") ?? Localizer.English,
            Theme = store.Get("theme") ?? Light,
            DownloadDirectory = store.Get("download_dir") ?? defaultDirectory,
            DefaultConnections = ParseInt(store.Get("connections"), 8),
            MaxConcurrentDownloads = ParseInt(store.Get("max_concurrent"), 3),
            GlobalSpeedLimitKBps = ParseInt(store.Get("speed_limit_kbps"), 0),
            AutoResumeDownloads = ParseBool(store.Get("auto_resume"), true) ? 1 : 0,
            MonitorClipboard = ParseBool(store.Get("monitor_clipboard"), false) ? 1 : 0,
            MinimizeToTray = ParseBool(store.Get("minimize_to_tray"), true) ? 1 : 0,
            CloseToTray = ParseBool(store.Get("close_to_tray"), true) ? 1 : 0,
            ShowNotifications = ParseBool(store.Get("notifications"), true) ? 1 : 0,
            ShowDownloadWindow = ParseBool(store.Get("show_dl_window"), true) ? 1 : 0,
            StartWithWindows = ParseBool(store.Get("start_with_windows"), true) ? 1 : 0,
            DefaultPriority = ParseInt(store.Get("default_priority"), 50),
        };
        s.Normalize(defaultDirectory);
        return s;
    }

    public void Save(ISettingsStore store)
    {
        store.Set("language", Language);
        store.Set("theme", Theme);
        store.Set("download_dir", DownloadDirectory);
        store.Set("connections", DefaultConnections.ToString(CultureInfo.InvariantCulture));
        store.Set("max_concurrent", MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture));
        store.Set("speed_limit_kbps", GlobalSpeedLimitKBps.ToString(CultureInfo.InvariantCulture));
        store.Set("auto_resume", AutoResumeDownloads != 0 ? "true" : "false");
        store.Set("monitor_clipboard", MonitorClipboard != 0 ? "true" : "false");
        store.Set("minimize_to_tray", MinimizeToTray != 0 ? "true" : "false");
        store.Set("close_to_tray", CloseToTray != 0 ? "true" : "false");
        store.Set("notifications", ShowNotifications != 0 ? "true" : "false");
        store.Set("show_dl_window", ShowDownloadWindow != 0 ? "true" : "false");
        store.Set("start_with_windows", StartWithWindows != 0 ? "true" : "false");
        store.Set("default_priority", DefaultPriority.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Forces every value into its legal range so a bad value can never reach the engine.</summary>
    public void Normalize(string defaultDirectory)
    {
        Language = Language == Localizer.Arabic ? Localizer.Arabic : Localizer.English;
        Theme = Theme == Dark ? Dark : Light;
        if (string.IsNullOrWhiteSpace(DownloadDirectory)) DownloadDirectory = defaultDirectory;
        DefaultConnections = EngineOptions.ClampConnections(DefaultConnections);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 32);
        GlobalSpeedLimitKBps = Math.Clamp(GlobalSpeedLimitKBps, 0, 10_000_000);
        AutoResumeDownloads = Math.Clamp(AutoResumeDownloads, 0, 1);
        MonitorClipboard = Math.Clamp(MonitorClipboard, 0, 1);
        MinimizeToTray = Math.Clamp(MinimizeToTray, 0, 1);
        CloseToTray = Math.Clamp(CloseToTray, 0, 1);
        ShowNotifications = Math.Clamp(ShowNotifications, 0, 1);
        ShowDownloadWindow = Math.Clamp(ShowDownloadWindow, 0, 1);
        StartWithWindows = Math.Clamp(StartWithWindows, 0, 1);
        DefaultPriority = Math.Clamp(DefaultPriority, -100, 200);
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool ParseBool(string? text, bool fallback) =>
        bool.TryParse(text, out var v) ? v : fallback;
}
