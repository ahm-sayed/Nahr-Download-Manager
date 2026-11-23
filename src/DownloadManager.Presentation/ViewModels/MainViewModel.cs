using System.Collections.ObjectModel;
using System.Windows.Input;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Engine;
using DownloadManager.Engine.Hls;
using DownloadManager.Engine.YouTube;
using DownloadManager.Presentation.Formatting;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;
using DownloadManager.Presentation.Services;
using DownloadManager.Presentation.Settings;

namespace DownloadManager.Presentation.ViewModels;

public enum AppPage { Downloads, Settings }

/// <summary>
/// Everything the main window shows and does. All members must be used from the UI thread; engine events are
/// marshalled through <see cref="IUiDispatcher"/>. It contains no WPF types, so it is fully testable.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDownloadActions, IAsyncDisposable
{
    private readonly IDownloadEngine _engine;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _ui;
    private readonly IShellService _shell;
    private readonly string _defaultDirectory;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = new();
    private readonly Dictionary<string, DownloadItemViewModel> _playlistGroups = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _removed = new();
    private readonly HashSet<Guid> _browserInterceptedIds = new();
    private readonly object _progressGate = new();
    private readonly Dictionary<Guid, DownloadProgress> _pendingProgress = new();
    private bool _progressDrainScheduled;
    private readonly EventHandler<DownloadProgress> _onProgress;
    private readonly EventHandler<DownloadItem> _onStatus;
    private readonly YouTubeService _youtube = new();
    private CancellationTokenSource? _addDialogProbeCts;

    private AppSettings _settings;
    private AppPage _page = AppPage.Downloads;
    private NavItem _selectedFilter;
    private NavItem _selectedCategory;
    private DownloadItemViewModel? _selected;
    private AddDownloadViewModel? _addDialog;
    private string _searchText = "";
    private string _statusMessage = "";
    private string _theme;
    private string _totalSpeedText = "—";
    private string _floatingTitleText = "";
    private string _floatingPercentText = "0%";
    private double _floatingPercent;
    private bool _hasFloatingTracker;
    private bool _isAllChecked;
    private bool _suppressSelectAllCascade;
    private bool _isDetailsOpen;
    private int _checkedCount;
    private int _activeCount;
    private bool _disposed;
    private bool _queuePaused;

    /// <summary>Raised when an extension-intercepted download fails at 0 bytes with HTTP 401/403/404/410 so the browser can download it directly.</summary>
    public event EventHandler<(Guid Id, string Url, string? FileName, bool RememberHost)>? BrowserFallbackRequested;

    public MainViewModel(IDownloadEngine engine, ISettingsStore settingsStore, AppSettings settings, Localizer loc,
        IUiDispatcher ui, IShellService shell)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _settings = settings;
        _ui = ui;
        _shell = shell;
        _defaultDirectory = settings.DownloadDirectory;
        _theme = settings.Theme;
        Loc = loc;

        Filters =
        [
            new NavItem("all", "filter_all", "\uE8FD", loc),
            new NavItem("active", "filter_active", "\uE896", loc),
            new NavItem("paused", "filter_paused", "\uE769", loc),
            new NavItem("completed", "filter_completed", "\uE73E", loc),
            new NavItem("failed", "filter_failed", "\uE783", loc),
        ];
        Categories = [new NavItem("", "cat_all", "\uE8B7", loc), .. Categorizer.All.Select(c => new NavItem(c, "cat_" + c, "\uE8A5", loc))];
        _selectedFilter = Filters[0];
        _selectedCategory = Categories[0];

        SettingsPage = new SettingsViewModel(SaveSettings, () => _shell.PickFolder(_settings.DownloadDirectory));
        SettingsPage.Load(settings);
        SettingsPage.SettingsSaved += model => SettingsChanged?.Invoke(this, model);

        AddUrlCommand = new RelayCommand(() => OpenAddDialog(null));
        ShowDownloadsCommand = new RelayCommand(() => CurrentPage = AppPage.Downloads);
        ShowSettingsCommand = new RelayCommand(() => CurrentPage = AppPage.Settings);
        PauseAllCommand = new AsyncRelayCommand(PauseAllAsync);
        ResumeAllCommand = new AsyncRelayCommand(ResumeAllAsync);
        PauseQueueCommand = new RelayCommand(() => IsQueuePaused = true);
        ResumeQueueCommand = new RelayCommand(() => IsQueuePaused = false);
        ClearCompletedCommand = new AsyncRelayCommand(ClearCompletedAsync);
        ToggleDetailsDrawerCommand = new RelayCommand(() => IsDetailsOpen = !IsDetailsOpen);
        ResumeCheckedCommand = new AsyncRelayCommand(ResumeCheckedAsync, () => _checkedCount > 0);
        PauseCheckedCommand = new AsyncRelayCommand(PauseCheckedAsync, () => _checkedCount > 0);
        CancelCheckedCommand = new AsyncRelayCommand(CancelCheckedAsync, () => _checkedCount > 0);
        RemoveCheckedCommand = new AsyncRelayCommand(RemoveCheckedAsync, () => _checkedCount > 0);
        DeleteCheckedFilesCommand = new AsyncRelayCommand(DeleteCheckedFilesAsync, () => _checkedCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        OpenActiveProgressCommand = new RelayCommand(() =>
        {
            var target = Downloads.FirstOrDefault(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Probing or DownloadStatus.Queued)
                      ?? Downloads.FirstOrDefault();
            if (target is not null) ShowProgressWindow(target);
        });

        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Localizer.IsRtl)) OnPropertyChanged(nameof(IsRtl));
            if (e.PropertyName == "Item[]") OnPropertyChanged(nameof(CheckedCountText));
        };
        Downloads.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ShowEmptyState)); OnPropertyChanged(nameof(TotalCount)); };

        _onStatus = (_, snapshot) => _ui.Post(() => ApplyStatus(snapshot));
        _onProgress = (_, progress) => EnqueueProgress(progress);
    }

    // ------------------------------------------------------------------ state exposed to the view

    public Localizer Loc { get; }
    public bool IsRtl => Loc.IsRtl;
    public SettingsViewModel SettingsPage { get; }

    /// <summary>The settings currently in effect (read-only snapshot of the latest saved model).</summary>
    public AppSettings Settings => _settings;
    public IReadOnlyList<NavItem> Filters { get; }
    public IReadOnlyList<NavItem> Categories { get; }

    /// <summary>All downloads, newest first.</summary>
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();

    /// <summary>The downloads that pass the current status filter, category and search text.</summary>
    public ObservableCollection<DownloadItemViewModel> VisibleDownloads { get; } = new();

    /// <summary>Raised when a standalone pre-download or progress window should be shown (without opening MainWindow).</summary>
    public event EventHandler<DownloadSessionViewModel>? DownloadWindowRequested;

    public string Theme
    {
        get => _theme;
        private set => Set(ref _theme, value);
    }

    public AppPage CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsDownloadsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public bool IsDownloadsPage => _page == AppPage.Downloads;
    public bool IsSettingsPage => _page == AppPage.Settings;

    public NavItem SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if ((object?)value is null || !Set(ref _selectedFilter, value)) return;
            Refilter();
        }
    }

    public NavItem SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if ((object?)value is null || !Set(ref _selectedCategory, value)) return;
            Refilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) Refilter();
        }
    }

    public DownloadItemViewModel? SelectedDownload
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ShowDetailsDrawer));
            if (value is { IsSingleItem: true } && _engine.Get(value.Id) is { } snapshot) value.Apply(snapshot);
        }
    }

    public bool HasSelection => _selected is { IsSingleItem: true };

    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        set
        {
            if (Set(ref _isDetailsOpen, value))
                OnPropertyChanged(nameof(ShowDetailsDrawer));
        }
    }

    public bool ShowDetailsDrawer => _isDetailsOpen && _selected is { IsSingleItem: true };

    // Multi-Select State & Bulk Commands
    public int CheckedCount
    {
        get => _checkedCount;
        private set
        {
            if (Set(ref _checkedCount, value))
            {
                OnPropertyChanged(nameof(HasCheckedItems));
                OnPropertyChanged(nameof(ShowStandardToolbar));
                OnPropertyChanged(nameof(CheckedCountText));
                foreach (var c in new[] { ResumeCheckedCommand, PauseCheckedCommand, CancelCheckedCommand, RemoveCheckedCommand, DeleteCheckedFilesCommand })
                    (c as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasCheckedItems => _checkedCount > 0;
    public bool ShowStandardToolbar => _checkedCount == 0;
    public string CheckedCountText => Loc.Format("sel_count", _checkedCount);

    public bool IsAllChecked
    {
        get => _isAllChecked;
        set
        {
            if (!Set(ref _isAllChecked, value)) return;
            if (_suppressSelectAllCascade) return;

            foreach (var row in VisibleDownloads)
            {
                if (row.IsPlaylistGroup)
                {
                    foreach (var child in row.Children) child.SetCheckedFromGroup(value);
                    row.SyncGroupCheckedFromChildren();
                }
                else
                {
                    row.SetCheckedFromGroup(value);
                }
            }
            RefreshSelectionCounts();
        }
    }

    public AddDownloadViewModel? AddDialog
    {
        get => _addDialog;
        private set
        {
            if (Set(ref _addDialog, value)) OnPropertyChanged(nameof(IsAddDialogOpen));
        }
    }

    public bool IsAddDialogOpen => _addDialog is not null;
    public bool ShowEmptyState => Downloads.Count == 0 && IsDownloadsPage;
    public int TotalCount => Downloads.Count;
    public int ActiveCount { get => _activeCount; private set => Set(ref _activeCount, value); }
    public string TotalSpeedText { get => _totalSpeedText; private set => Set(ref _totalSpeedText, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    // Live state for the Always-On-Top On-Screen Floating Progress Tracker Widget
    public bool HasFloatingTracker { get => _hasFloatingTracker; private set => Set(ref _hasFloatingTracker, value); }
    public double FloatingPercent { get => _floatingPercent; private set => Set(ref _floatingPercent, value); }
    public string FloatingPercentText { get => _floatingPercentText; private set => Set(ref _floatingPercentText, value); }
    public string FloatingTitleText { get => _floatingTitleText; private set => Set(ref _floatingTitleText, value); }
    public ICommand OpenActiveProgressCommand { get; }

    public bool IsQueuePaused
    {
        get => _queuePaused;
        set
        {
            if (!Set(ref _queuePaused, value)) return;
            _engine.SetQueuePaused(value);
        }
    }

    public ICommand AddUrlCommand { get; }
    public ICommand ShowDownloadsCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand PauseQueueCommand { get; }
    public ICommand ResumeQueueCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand ToggleDetailsDrawerCommand { get; }
    public ICommand ResumeCheckedCommand { get; }
    public ICommand PauseCheckedCommand { get; }
    public ICommand CancelCheckedCommand { get; }
    public ICommand RemoveCheckedCommand { get; }
    public ICommand DeleteCheckedFilesCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; }
    public bool NotificationsEnabled { get; set; }
    public INotificationService? Notifications { get; set; }

    /// <summary>Test hook: allows injecting a fake clipboard monitor for unit tests.</summary>
    internal object? ClipboardMonitor { get; set; }

    /// <summary>Raised after settings are saved in the dialog, so the shell can react (clipboard, tray, toasts).</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync()
    {
        ApplySettings(_settings);
        await _engine.RecoverAsync(_settings.AutoResumeDownloads != 0).ConfigureAwait(true);
        foreach (var snapshot in _engine.GetAll()) AddViewModel(snapshot);
        _engine.StatusChanged += _onStatus;
        _engine.ProgressChanged += _onProgress;
        Refilter();
        RefreshCounts();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _addDialogProbeCts?.Cancel();
        _addDialogProbeCts?.Dispose();
        _engine.StatusChanged -= _onStatus;
        _engine.ProgressChanged -= _onProgress;
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ standalone download window (IDM-style)

    /// <summary>
    /// Opens a standalone pre-download confirmation window (with quality selection &amp; Start button) without opening MainWindow.
    /// </summary>
    public DownloadSessionViewModel OpenStandaloneDownloadPrompt(ExtensionDownloadRequest request)
    {
        var session = new DownloadSessionViewModel(
            request,
            _settings.DownloadDirectory,
            Loc,
            dir => _shell.PickFolder(dir ?? _settings.DownloadDirectory),
            CommitStandaloneSessionAsync,
            (id, bps) => _engine.SetSpeedLimit(id, bps));

        session.BackgroundProbeTask = ProbeSessionInBackgroundAsync(session);
        DownloadWindowRequested?.Invoke(this, session);
        return session;
    }

    public void ShowProgressWindow(DownloadItemViewModel item)
    {
        var target = item.IsPlaylistGroup
            ? (item.Children.FirstOrDefault(c => c.IsDownloading) ?? item.Children.FirstOrDefault())
            : item;
        if (target is null) return;

        var session = new DownloadSessionViewModel(
            target,
            Loc,
            dir => _shell.PickFolder(dir ?? _settings.DownloadDirectory),
            (id, bps) => _engine.SetSpeedLimit(id, bps));
        DownloadWindowRequested?.Invoke(this, session);
    }

    private async Task ProbeSessionInBackgroundAsync(DownloadSessionViewModel session)
    {
        var req = session.Request;
        if (req is null) return;

        session.IsProbing = true;
        try
        {
            var isYoutube = YouTubeService.IsYouTubeUrl(req.Url);
            var isGenericExtractor = !isYoutube && string.Equals(req.MediaType, "ytdlp", StringComparison.OrdinalIgnoreCase);

            if (isYoutube || isGenericExtractor)
            {
                if (isGenericExtractor && !_youtube.CanRunYtDlp)
                {
                    session.ProbeStatus = Loc["yt_not_available"];
                    return;
                }

                session.ProbeStatus = Loc["yt_extracting"];
                if (isYoutube && YouTubeService.IsPlaylistUrl(req.Url))
                {
                    var playlist = await _youtube.GetPlaylistInfoAsync(req.Url).ConfigureAwait(true);
                    session.ApplyYouTubePlaylistProbe(playlist);
                }
                else
                {
                    var info = await _youtube.GetVideoInfoAsync(req.Url).ConfigureAwait(true);
                    session.ApplyYouTubeVideoProbe(info);
                    session.ProbeStatus = "";
                }
            }
            else if (HlsStreamService.LooksLikeHlsUrl(req.Url, req.MediaType) || !string.IsNullOrWhiteSpace(req.MasterUrl))
            {
                session.ProbeStatus = Loc["yt_extracting"];
                var probeUrl = !string.IsNullOrWhiteSpace(req.MasterUrl) ? req.MasterUrl! : req.Url;
                var variants = await _engine.ProbeHlsQualitiesAsync(probeUrl, req.Referrer, req.Origin, req.UserAgent).ConfigureAwait(true);
                session.ApplyHlsVariantsProbe(variants);
                session.ProbeStatus = "";
            }
            else if (req.FromBrowserInterception && (LooksLikeSignedOrTokenizedUrl(session.Url) || req.FileSize is > 0))
            {
                // Avoid burning single-use signed tokens (e.g. ?token= / ?X-Amz-Signature=) or duplicating
                // probes before the user clicks Start Download.
                session.ProbeStatus = "";
            }
            else
            {
                session.ProbeStatus = Loc["dl_probing"];
                var probeHeaders = BuildExtensionHeaders(req);
                var (name, len, contentType) = await _engine.QuickProbeAsync(session.Url, req.Referrer, req.UserAgent, probeHeaders).ConfigureAwait(true);
                if (contentType is not null && contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                {
                    var variants = await _engine.ProbeHlsQualitiesAsync(session.Url, req.Referrer, req.Origin, req.UserAgent).ConfigureAwait(true);
                    session.ApplyHlsVariantsProbe(variants);
                }
                else
                {
                    session.ApplyHttpProbe(name, len, contentType);
                }
                session.ProbeStatus = "";
            }
        }
        catch (Exception ex)
        {
            session.ProbeStatus = "";
            if (YouTubeService.IsYouTubeUrl(req.Url))
                session.PreErrorMessage = Loc.Format("yt_error", ex.Message);
        }
        finally
        {
            session.IsProbing = false;
        }
    }

    private async Task<DownloadItemViewModel?> CommitStandaloneSessionAsync(DownloadSessionViewModel session, bool startNow)
    {
        var directory = string.IsNullOrWhiteSpace(session.DestinationDirectory)
            ? _settings.DownloadDirectory
            : session.DestinationDirectory;
        var req = session.Request;
        var url = session.Url;
        var origUrl = req?.Url ?? url;
        var chosenQuality = session.SelectedQuality;
        var wantAudio = session.IsAudioMode || (chosenQuality?.IsAudioMode ?? false);

        var isYoutube = YouTubeService.IsYouTubeUrl(origUrl);
        var isYtOrExtractor = isYoutube ||
                              (string.Equals(req?.MediaType, "ytdlp", StringComparison.OrdinalIgnoreCase) && _youtube.CanRunYtDlp);

        if (isYtOrExtractor)
        {
            if (session.BackgroundProbeTask is not null)
            {
                try { await session.BackgroundProbeTask.ConfigureAwait(true); } catch { /* handled below */ }
            }

            if (isYoutube && YouTubeService.IsPlaylistUrl(origUrl))
            {
                var playlist = session.ProbedPlaylistInfo ?? await _youtube.GetPlaylistInfoAsync(origUrl).ConfigureAwait(true);
                var selectedEntries = session.HasPlaylistEntries
                    ? session.GetSelectedPlaylistEntries()
                    : playlist.Entries;
                if (selectedEntries.Count == 0)
                    throw new InvalidOperationException(Loc["pl_none_selected"]);

                var groupId = "pl:" + (string.IsNullOrWhiteSpace(playlist.Id)
                    ? (YouTubeService.ExtractPlaylistId(origUrl) ?? Guid.NewGuid().ToString("N")[..10])
                    : playlist.Id);
                var groupTitle = !string.IsNullOrWhiteSpace(session.FileName)
                    ? SanitizeFileName(session.FileName)
                    : (!string.IsNullOrWhiteSpace(playlist.Title) ? playlist.Title : "YouTube Playlist");
                var playlistDir = Path.Combine(directory, SanitizeFileName(groupTitle));

                var firstEntry = selectedEntries[0];
                var firstVideoUrl = firstEntry.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? firstEntry.Url
                    : $"https://www.youtube.com/watch?v={firstEntry.VideoId}";
                var firstInfo = await _youtube.GetVideoInfoAsync(firstVideoUrl).ConfigureAwait(true);
                var firstFmt = PickFormatForChoice(firstInfo, chosenQuality, wantAudio);
                DownloadItemViewModel? firstCreated = null;
                if (firstFmt is not null)
                {
                    firstCreated = await EnqueueYouTubeFormatAsync(
                        firstVideoUrl, firstInfo, firstFmt, playlistDir, null, startNow, chosenQuality, wantAudio, groupId, groupTitle).ConfigureAwait(true);
                }

                if (selectedEntries.Count > 1)
                {
                    var rest = new YouTubePlaylistInfo
                    {
                        Id = playlist.Id,
                        Title = groupTitle,
                        Uploader = playlist.Uploader,
                        Entries = selectedEntries.Skip(1).ToList(),
                    };
                    _ = AddYouTubePlaylistAsync(origUrl, rest, directory, chosenQuality, wantAudio, startNow, groupId, groupTitle);
                }
                return firstCreated;
            }

            var info = session.ProbedVideoInfo ?? await _youtube.GetVideoInfoAsync(origUrl).ConfigureAwait(true);
            var format = PickFormatForChoice(info, chosenQuality, wantAudio);
            if (format is null) throw new InvalidOperationException("No downloadable media stream found.");

            var created = await EnqueueYouTubeFormatAsync(
                origUrl, info, format, directory, session.FileName, startNow, chosenQuality, wantAudio).ConfigureAwait(true);
            return created;
        }

        // HLS (.m3u8 / #EXTM3U) or Normal file / HTML5 video/audio stream
        var isHls = (chosenQuality?.IsHlsStream ?? false) || HlsStreamService.LooksLikeHlsUrl(url, req?.MediaType);
        var targetExt = wantAudio ? (chosenQuality?.Extension ?? "mp3") : "mp4";
        var finalName = string.IsNullOrWhiteSpace(session.FileName) ? null : session.FileName;
        if ((isHls || wantAudio) && finalName is not null)
            finalName = EnsureExtension(SanitizeFileName(finalName), targetExt);

        Func<string, CancellationToken, Task>? directAudioPostProcess = null;
        if (!isHls && wantAudio && _youtube.HasFfmpeg)
        {
            var br = chosenQuality?.AudioBitrateKbps ?? 192;
            directAudioPostProcess = (partPath, ct) => _youtube.ConvertToAudioAsync(partPath, targetExt, br, ct);
        }

        var item = await _engine.AddAsync(new AddDownloadRequest(url, directory)
        {
            FileName = finalName,
            Connections = _settings.DefaultConnections,
            KnownTotalBytes = isHls ? null : session.KnownBytes,
            SkipInitialProbe = isHls || session.KnownBytes is > 0 || (req?.FromBrowserInterception == true && LooksLikeSignedOrTokenizedUrl(url)),
            IsHlsStream = isHls,
            ExtractAudioOnly = isHls && wantAudio,
            AudioFormatExt = targetExt,
            AudioBitrateKbps = chosenQuality?.AudioBitrateKbps is > 0 ? chosenQuality.AudioBitrateKbps : 192,
            CompanionAudioUrl = chosenQuality?.CompanionAudioUrl,
            FfmpegPath = _youtube.FfmpegPath,
            PostProcessAsync = directAudioPostProcess,
            Referer = req?.Referrer,
            Origin = req?.Origin,
            UserAgent = req?.UserAgent,
            ExtraHeaders = BuildExtensionHeaders(req),
        }).ConfigureAwait(true);

        if (req?.FromBrowserInterception == true)
            _browserInterceptedIds.Add(item.Id);

        var vm = GetOrCreateViewModel(item);
        if (startNow) await _engine.StartAsync(item.Id).ConfigureAwait(true);
        return vm;
    }

    private static Dictionary<string, string>? BuildExtensionHeaders(ExtensionDownloadRequest? req)
    {
        if (string.IsNullOrWhiteSpace(req?.Cookies)) return null;
        return new Dictionary<string, string> { ["Cookie"] = req.Cookies! };
    }

    private static bool LooksLikeSignedOrTokenizedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var qIdx = url.IndexOf('?');
        if (qIdx < 0) return false;
        var query = url[qIdx..];
        return query.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("signature=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("sig=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("expires=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("auth=", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ adding downloads (MainWindow dialog)

    public void OpenAddDialog(string? prefill)
    {
        _addDialogProbeCts?.Cancel();
        _addDialogProbeCts?.Dispose();
        _addDialogProbeCts = null;

        AddDownloadViewModel? created = null;
        created = new AddDownloadViewModel(
            _settings.DownloadDirectory,
            _settings.DefaultConnections,
            ConfirmAddAsync,
            () =>
            {
                _addDialogProbeCts?.Cancel();
                AddDialog = null;
            },
            () => _shell.PickFolder(AddDialog?.DestinationDirectory),
            Loc,
            text => TriggerAddDialogYouTubeProbe(created, text));

        if (!string.IsNullOrEmpty(prefill))
            created.UrlsText = prefill;

        AddDialog = created;
        CurrentPage = AppPage.Downloads;
    }

    private void TriggerAddDialogYouTubeProbe(AddDownloadViewModel? dialog, string? text)
    {
        _addDialogProbeCts?.Cancel();
        _addDialogProbeCts?.Dispose();
        _addDialogProbeCts = null;

        if (dialog is null) return;
        var urls = AddDownloadViewModel.ParseUrls(text, out _);
        if (urls.Count != 1 || !YouTubeService.IsYouTubeUrl(urls[0]) || YouTubeService.IsPlaylistUrl(urls[0]))
        {
            dialog.CachedVideoInfo = null;
            return;
        }

        var singleYtUrl = urls[0];
        var cts = new CancellationTokenSource();
        _addDialogProbeCts = cts;
        _ = ProbeAddDialogYouTubeAsync(dialog, singleYtUrl, cts.Token);
    }

    private async Task ProbeAddDialogYouTubeAsync(AddDownloadViewModel dialog, string url, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct).ConfigureAwait(true); // debounce typing
            dialog.IsFetchingFormats = true;
            var info = await _youtube.GetVideoInfoAsync(url, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || !ReferenceEquals(AddDialog, dialog)) return;

            dialog.CachedVideoInfo = info;
            var videoFormats = info.QualityOptions.Where(f => f.HasVideo).ToList();
            if (videoFormats.Count == 0) videoFormats = info.Formats.Where(f => f.HasVideo).ToList();

            var videoChoices = videoFormats.Select(fmt => new QualityChoice
            {
                Key = fmt.Height?.ToString() ?? fmt.FormatId,
                Label = fmt.DisplayLabel,
                YouTubeFormat = fmt,
                DirectMediaUrl = fmt.DirectUrl,
                Extension = fmt.EffectiveExtension,
                EstimatedBytes = fmt.TotalEstimatedBytes,
            }).ToList();

            var bestAudio = info.AudioOptions.FirstOrDefault() ?? info.Formats.FirstOrDefault(f => !f.HasVideo && f.HasAudio);
            var lowAudio = info.AudioOptions.LastOrDefault();
            var audioChoices = QualityChoice.CreateAudioQualityOptions(Loc, info.Duration, bestAudio, lowAudio);

            if (videoChoices.Count > 0 || audioChoices.Count > 0)
            {
                dialog.SetProbedChoices(
                    videoChoices.Count > 0 ? videoChoices : QualityChoice.CreateYouTubeVideoPresets(Loc),
                    audioChoices);
            }
        }
        catch
        {
            // Keep preset options if probe fails or is cancelled
        }
        finally
        {
            if (ReferenceEquals(AddDialog, dialog)) dialog.IsFetchingFormats = false;
        }
    }

    /// <summary>Drag-and-drop / paste entry point: opens the dialog pre-filled when the text contains links.</summary>
    public bool HandleDroppedText(string? text)
    {
        if (AddDownloadViewModel.ParseUrls(text, out _).Count == 0) return false;
        OpenAddDialog(text);
        return true;
    }

    private async Task ConfirmAddAsync()
    {
        var dialog = AddDialog;
        if (dialog is null) return;

        var urls = AddDownloadViewModel.ParseUrls(dialog.UrlsText, out var skipped);
        if (urls.Count == 0)
        {
            dialog.Message = Loc["add_no_valid"];
            return;
        }

        _addDialogProbeCts?.Cancel();
        var directory = string.IsNullOrWhiteSpace(dialog.DestinationDirectory) ? _settings.DownloadDirectory : dialog.DestinationDirectory;
        var connections = dialog.Connections;
        var startImmediately = dialog.StartImmediately;
        var selectedQuality = dialog.SelectedQuality;
        var wantAudio = dialog.IsAudioMode || (selectedQuality?.IsAudioMode ?? false);
        var cachedInfo = dialog.CachedVideoInfo;
        AddDialog = null;

        var normalUrls = new List<string>();
        var youtubeVideoUrls = new List<string>();
        var youtubePlaylistUrls = new List<string>();

        foreach (var url in urls)
        {
            if (YouTubeService.IsYouTubeUrl(url))
            {
                if (YouTubeService.IsPlaylistUrl(url))
                    youtubePlaylistUrls.Add(url);
                else
                    youtubeVideoUrls.Add(url);
            }
            else
            {
                normalUrls.Add(url);
            }
        }

        var failed = 0;
        var showSingleProgressWindow = urls.Count == 1 && startImmediately && _settings.ShowDownloadWindow != 0;

        if (normalUrls.Count > 0)
        {
            var (normFailed, firstVm) = await AddManyAsync(normalUrls, directory, connections, startImmediately).ConfigureAwait(true);
            failed += normFailed;
            if (showSingleProgressWindow && firstVm is not null)
                ShowProgressWindow(firstVm);
        }

        if (youtubeVideoUrls.Count > 0 || youtubePlaylistUrls.Count > 0)
        {
            var (ytFailed, firstYtVm) = await AddYouTubeAsync(
                youtubeVideoUrls, youtubePlaylistUrls, directory, startImmediately, selectedQuality, wantAudio, cachedInfo).ConfigureAwait(true);
            failed += ytFailed;
            if (showSingleProgressWindow && firstYtVm is not null)
                ShowProgressWindow(firstYtVm);
        }

        var notes = new List<string>();
        if (skipped > 0) notes.Add(Loc.Format("add_skipped", skipped));
        if (failed > 0) notes.Add(Loc.Format("add_failed", failed));
        StatusMessage = string.Join(" ", notes);
    }

    private static YouTubeFormat? PickFormatForChoice(YouTubeVideoInfo info, QualityChoice? choice, bool wantAudio)
    {
        if (wantAudio)
        {
            if (choice?.YouTubeFormat is { HasVideo: false, HasAudio: true } af) return af;
            if (choice?.AudioBitrateKbps is > 0 and <= 80 && info.AudioOptions.Count > 1)
                return info.AudioOptions[^1];
            return info.AudioOptions.FirstOrDefault()
                ?? YouTubeService.PickFormat(info, "audio")
                ?? info.Formats.FirstOrDefault();
        }

        return choice?.YouTubeFormat
            ?? YouTubeService.PickFormat(info, choice?.Key ?? "best")
            ?? info.Formats.FirstOrDefault();
    }

    private async Task<(int Failed, DownloadItemViewModel? FirstCreated)> AddYouTubeAsync(
        IReadOnlyList<string> videoUrls,
        IReadOnlyList<string> playlistUrls,
        string directory,
        bool start,
        QualityChoice? selectedQuality,
        bool wantAudio,
        YouTubeVideoInfo? cachedSingleInfo)
    {
        var failed = 0;
        DownloadItemViewModel? firstCreated = null;
        StatusMessage = Loc["yt_extracting"];

        foreach (var url in videoUrls)
        {
            try
            {
                var info = cachedSingleInfo is not null && videoUrls.Count == 1
                    ? cachedSingleInfo
                    : await _youtube.GetVideoInfoAsync(url).ConfigureAwait(true);

                var format = PickFormatForChoice(info, videoUrls.Count == 1 ? selectedQuality : new QualityChoice
                {
                    Key = selectedQuality?.Key ?? "best",
                    Label = selectedQuality?.Label ?? "",
                    IsAudioMode = wantAudio,
                    AudioBitrateKbps = selectedQuality?.AudioBitrateKbps ?? 192,
                    Extension = selectedQuality?.Extension,
                }, wantAudio);

                if (format is null) { failed++; continue; }

                var vm = await EnqueueYouTubeFormatAsync(url, info, format, directory, null, start, selectedQuality, wantAudio).ConfigureAwait(true);
                firstCreated ??= vm;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        foreach (var url in playlistUrls)
        {
            failed += await AddYouTubePlaylistAsync(url, null, directory, selectedQuality, wantAudio, start).ConfigureAwait(true);
        }

        StatusMessage = "";
        return (failed, firstCreated);
    }

    private async Task<int> AddYouTubePlaylistAsync(
        string playlistUrl,
        YouTubePlaylistInfo? preloaded,
        string directory,
        QualityChoice? selectedQuality,
        bool wantAudio,
        bool start,
        string? explicitGroupId = null,
        string? explicitGroupTitle = null)
    {
        var failed = 0;
        try
        {
            var playlist = preloaded ?? await _youtube.GetPlaylistInfoAsync(playlistUrl).ConfigureAwait(true);
            StatusMessage = Loc.Format("yt_downloading_playlist", playlist.Entries.Count);

            var groupId = explicitGroupId ?? ("pl:" + (string.IsNullOrWhiteSpace(playlist.Id)
                ? (YouTubeService.ExtractPlaylistId(playlistUrl) ?? Guid.NewGuid().ToString("N")[..10])
                : playlist.Id));
            var groupTitle = explicitGroupTitle ?? (!string.IsNullOrWhiteSpace(playlist.Title) ? playlist.Title : "YouTube Playlist");
            var playlistDir = Path.Combine(directory, SanitizeFileName(groupTitle));

            var templateChoice = new QualityChoice
            {
                Key = selectedQuality?.Key ?? "best",
                Label = selectedQuality?.Label ?? "",
                IsAudioMode = wantAudio,
                AudioBitrateKbps = selectedQuality?.AudioBitrateKbps ?? 192,
                Extension = selectedQuality?.Extension,
            };

            foreach (var entry in playlist.Entries)
            {
                try
                {
                    var videoUrl = entry.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? entry.Url
                        : $"https://www.youtube.com/watch?v={entry.VideoId}";

                    var info = await _youtube.GetVideoInfoAsync(videoUrl).ConfigureAwait(true);
                    var format = PickFormatForChoice(info, templateChoice, wantAudio);
                    if (format is null) { failed++; continue; }

                    await EnqueueYouTubeFormatAsync(
                        videoUrl, info, format, playlistDir, null, start, templateChoice, wantAudio, groupId, groupTitle).ConfigureAwait(true);
                }
                catch
                {
                    failed++;
                }
            }
            StatusMessage = "";
        }
        catch
        {
            failed++;
        }
        return failed;
    }

    private async Task<DownloadItemViewModel> EnqueueYouTubeFormatAsync(
        string videoUrl,
        YouTubeVideoInfo info,
        YouTubeFormat format,
        string directory,
        string? customFileName,
        bool start,
        QualityChoice? chosenQuality = null,
        bool wantAudio = false,
        string? groupId = null,
        string? groupTitle = null)
    {
        var directUrl = await _youtube.GetDirectUrlAsync(videoUrl, format).ConfigureAwait(true);
        var ext = wantAudio ? (chosenQuality?.Extension ?? "mp3") : format.EffectiveExtension;
        var fileName = !string.IsNullOrWhiteSpace(customFileName)
            ? EnsureExtension(SanitizeFileName(customFileName), ext)
            : $"{SanitizeFileName(info.Title)}.{ext}";

        Func<string, CancellationToken, Task>? postProcess = null;
        if (wantAudio && _youtube.HasFfmpeg)
        {
            var bitrate = chosenQuality?.AudioBitrateKbps ?? 192;
            var needsConvert = !string.Equals(ext, format.Extension, StringComparison.OrdinalIgnoreCase) || bitrate > 0 || format.HasVideo;
            if (needsConvert)
            {
                postProcess = (partPath, ct) => _youtube.ConvertToAudioAsync(partPath, ext, bitrate, ct);
            }
        }
        else if (!wantAudio && format.NeedsAudioMerge && _youtube.HasFfmpeg && !string.IsNullOrWhiteSpace(format.AudioDirectUrl))
        {
            var audioUrl = format.AudioDirectUrl!;
            var ua = format.UserAgent;
            postProcess = (partPath, ct) => _youtube.MergeAudioIntoPartAsync(partPath, audioUrl, ua, ct);
        }

        var item = await _engine.AddAsync(new AddDownloadRequest(directUrl, directory)
        {
            FileName = fileName,
            Connections = _settings.DefaultConnections,
            KnownTotalBytes = format.FileSizeApprox,
            SkipInitialProbe = true,
            UserAgent = format.UserAgent,
            PostProcessAsync = postProcess,
            GroupId = groupId,
            GroupTitle = groupTitle,
        }).ConfigureAwait(true);

        var vm = GetOrCreateViewModel(item);
        if (start) await _engine.StartAsync(item.Id).ConfigureAwait(true);
        return vm;
    }

    private static string EnsureExtension(string fileName, string ext)
    {
        var cleanExt = "." + ext.TrimStart('.');
        if (fileName.EndsWith(cleanExt, StringComparison.OrdinalIgnoreCase))
            return fileName;

        var existingExt = Path.GetExtension(fileName);
        if (existingExt.ToLowerInvariant() is ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".ts"
            or ".m3u8" or ".mp3" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".flac" or ".aac")
        {
            return Path.ChangeExtension(fileName, cleanExt);
        }

        return fileName + cleanExt;
    }

    /// <summary>Removes characters that are invalid in file names.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
            else sb.Append('_');
        }
        if (sb.Length > 200) sb.Length = 200;
        return sb.ToString().Trim();
    }

    private async Task<(int Failed, DownloadItemViewModel? FirstCreated)> AddManyAsync(
        IReadOnlyList<string> urls, string directory, int connections, bool start)
    {
        var failed = 0;
        DownloadItemViewModel? firstVm = null;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(urls.Select(async url =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                var item = await _engine.AddAsync(new AddDownloadRequest(url, directory) { Connections = connections }).ConfigureAwait(true);
                var vm = GetOrCreateViewModel(item);
                firstVm ??= vm;
                if (start) await _engine.StartAsync(item.Id).ConfigureAwait(true);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(true);
        return (failed, firstVm);
    }

    // ------------------------------------------------------------------ engine events

    private void ApplyStatus(DownloadItem snapshot)
    {
        if (_disposed || _removed.Contains(snapshot.Id)) return;
        if (_byId.TryGetValue(snapshot.Id, out var vm))
        {
            var before = vm.Status;
            vm.Apply(snapshot);
            if (!IsTerminal(before) && IsTerminal(snapshot.Status))
            {
                var err = snapshot.ErrorMessage ?? "";
                var isAuthOrTokenRejection =
                    snapshot.DownloadedBytes == 0 &&
                    (err.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) ||
                     err.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
                     err.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase) ||
                     err.Contains("HTTP 410", StringComparison.OrdinalIgnoreCase));

                if (_browserInterceptedIds.Remove(snapshot.Id)
                    && snapshot.Status == DownloadStatus.Failed
                    && isAuthOrTokenRejection)
                {
                    var rememberHost = err.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) ||
                                       err.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase);
                    BrowserFallbackRequested?.Invoke(this, (snapshot.Id, snapshot.Url, snapshot.FileName, rememberHost));
                    // Remove the dead 0-byte stub silently so Nahr stays clean while Chrome finishes the download
                    _ = RemoveSilentAfterFallbackAsync(snapshot.Id);
                    Refilter();
                    RefreshCounts();
                    return;
                }
                NotifyFinished(vm);
            }
        }
        else
        {
            AddViewModel(snapshot);
        }
        Refilter();
        RefreshCounts();
    }

    private async Task RemoveSilentAfterFallbackAsync(Guid id)
    {
        try
        {
            _removed.Add(id);
            if (_byId.Remove(id, out var vm))
            {
                Downloads.Remove(vm);
                if (ReferenceEquals(SelectedDownload, vm)) SelectedDownload = null;
            }
            await _engine.RemoveAsync(id, deleteFile: false).ConfigureAwait(true);
            Refilter();
            RefreshCounts();
        }
        catch { /* best effort cleanup */ }
    }

    /// <summary>True for the states a download never leaves on its own.</summary>
    private static bool IsTerminal(DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;

    /// <summary>Shows one notification per terminal transition, never for cancelled downloads.</summary>
    private void NotifyFinished(DownloadItemViewModel item)
    {
        if (!NotificationsEnabled || Notifications is null) return;

        if (item.Status == DownloadStatus.Completed)
        {
            var body = string.IsNullOrWhiteSpace(item.FilePath) ? Loc["notif_done_body"] : item.FilePath;
            if (item.NeedsHtmlWarning)
            {
                body = Loc["html_warning"] + " " + body;
            }
            Notifications.Show(Loc.Format("notif_done_title", item.FileName), body);
        }
        else if (item.Status == DownloadStatus.Failed)
        {
            var reason = string.IsNullOrWhiteSpace(item.ErrorMessage) ? Loc["notif_failed_body"] : item.ErrorMessage;
            Notifications.Show(Loc.Format("notif_failed_title", item.FileName), reason, isError: true);
        }
    }

    private void EnqueueProgress(DownloadProgress progress)
    {
        bool schedule;
        lock (_progressGate)
        {
            _pendingProgress[progress.Id] = progress;
            schedule = !_progressDrainScheduled;
            _progressDrainScheduled = true;
        }
        if (schedule) _ui.Post(DrainPendingProgress);
    }

    private void DrainPendingProgress()
    {
        List<DownloadProgress> batch;
        lock (_progressGate)
        {
            _progressDrainScheduled = false;
            if (_pendingProgress.Count == 0) return;
            batch = new List<DownloadProgress>(_pendingProgress.Values);
            _pendingProgress.Clear();
        }
        if (_disposed) return;

        var anyApplied = false;
        HashSet<string>? dirtyGroups = null;
        foreach (var progress in batch)
        {
            if (!_byId.TryGetValue(progress.Id, out var vm)) continue;
            vm.ApplyProgress(progress);
            if (_engine.Get(progress.Id) is { } snapshot)
            {
                vm.UpdateSegments(snapshot.Segments);
            }
            if (!string.IsNullOrWhiteSpace(vm.GroupId))
            {
                dirtyGroups ??= new HashSet<string>(StringComparer.Ordinal);
                dirtyGroups.Add(vm.GroupId!);
            }
            anyApplied = true;
        }

        if (dirtyGroups is not null)
        {
            foreach (var gid in dirtyGroups)
            {
                if (_playlistGroups.TryGetValue(gid, out var groupVm))
                    groupVm.RefreshPlaylistAggregate();
            }
        }

        if (anyApplied) UpdateTotals();
    }

    private DownloadItemViewModel GetOrCreateViewModel(DownloadItem snapshot)
    {
        if (_byId.TryGetValue(snapshot.Id, out var existing))
        {
            existing.Apply(snapshot);
            return existing;
        }
        var vm = AddViewModel(snapshot);
        Refilter();
        RefreshCounts();
        return vm;
    }

    private DownloadItemViewModel AddViewModel(DownloadItem snapshot)
    {
        var vm = new DownloadItemViewModel(snapshot, Loc, this);
        _byId[vm.Id] = vm;
        var index = 0;                                       // newest first
        while (index < Downloads.Count && Downloads[index].CreatedAt >= vm.CreatedAt) index++;
        Downloads.Insert(index, vm);
        return vm;
    }

    // ------------------------------------------------------------------ filtering & playlist grouping

    private bool MatchesStatusAndCategory(DownloadItemViewModel d)
    {
        var statusOk = _selectedFilter.Key switch
        {
            "active" => d.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading,
            "paused" => d.Status == DownloadStatus.Paused,
            "completed" => d.Status == DownloadStatus.Completed,
            "failed" => d.Status == DownloadStatus.Failed,
            _ => true,
        };
        if (!statusOk) return false;
        return _selectedCategory.Key.Length == 0 || d.CategoryKey == _selectedCategory.Key;
    }

    private static bool MatchesQuery(DownloadItemViewModel d, string query) =>
        query.Length == 0
        || d.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || d.Url.Contains(query, StringComparison.OrdinalIgnoreCase);

    private bool Matches(DownloadItemViewModel d) =>
        MatchesStatusAndCategory(d) && MatchesQuery(d, _searchText.Trim());

    private void Refilter()
    {
        var desired = new List<DownloadItemViewModel>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var query = _searchText.Trim();

        foreach (var d in Downloads)
        {
            if (string.IsNullOrWhiteSpace(d.GroupId))
            {
                if (Matches(d)) desired.Add(d);
                continue;
            }

            var gid = d.GroupId!;
            if (!seenGroups.Add(gid)) continue;

            // Keep playlist videos in natural chronological order (episode 1, 2, 3...) inside the playlist container
            var allMembers = Downloads.Where(x => x.GroupId == gid).OrderBy(x => x.CreatedAt).ToList();
            if (!_playlistGroups.TryGetValue(gid, out var groupVm))
            {
                var title = allMembers.Select(m => m.GroupTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "Playlist";
                groupVm = new DownloadItemViewModel(gid, title, Loc, this);
                _playlistGroups[gid] = groupVm;
            }

            var latestTitle = allMembers.Select(m => m.GroupTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var titleMatchesQuery = query.Length > 0 && (latestTitle ?? groupVm.FileName).Contains(query, StringComparison.OrdinalIgnoreCase);
            var visibleMembers = allMembers
                .Where(m => MatchesStatusAndCategory(m) && (query.Length == 0 || titleMatchesQuery || MatchesQuery(m, query)))
                .ToList();

            if (visibleMembers.Count == 0) continue;

            SyncCollection(groupVm.Children, visibleMembers);
            groupVm.RefreshPlaylistAggregate(latestTitle);
            if (query.Length > 0 && !titleMatchesQuery) groupVm.IsExpanded = true;
            desired.Add(groupVm);
        }

        // Remove stale playlist groups whose items were all removed
        foreach (var oldGid in _playlistGroups.Keys.Where(k => Downloads.All(x => x.GroupId != k)).ToList())
            _playlistGroups.Remove(oldGid);

        SyncCollection(VisibleDownloads, desired);
        RefreshSelectionCounts();
    }

    private static void SyncCollection(ObservableCollection<DownloadItemViewModel> target, IReadOnlyList<DownloadItemViewModel> desired)
    {
        var wanted = new HashSet<DownloadItemViewModel>(desired);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var existing = target.IndexOf(desired[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, desired[i]);
        }
    }

    private void RefreshCounts()
    {
        foreach (var f in Filters)
            f.Count = Downloads.Count(d => f.Key switch
            {
                "active" => d.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading,
                "paused" => d.Status == DownloadStatus.Paused,
                "completed" => d.Status == DownloadStatus.Completed,
                "failed" => d.Status == DownloadStatus.Failed,
                _ => true,
            });
        foreach (var c in Categories)
            c.Count = c.Key.Length == 0 ? Downloads.Count : Downloads.Count(d => d.CategoryKey == c.Key);
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var speed = 0.0;
        var active = 0;
        var inFlight = new List<DownloadItemViewModel>();
        foreach (var d in Downloads)
        {
            speed += d.Speed;
            if (d.Status is DownloadStatus.Probing or DownloadStatus.Downloading) active++;
            if (d.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading)
                inFlight.Add(d);
        }
        ActiveCount = active;
        TotalSpeedText = Format.Speed(speed);

        HasFloatingTracker = inFlight.Count > 0;
        if (inFlight.Count > 0)
        {
            var primary = inFlight.FirstOrDefault(d => d.Status == DownloadStatus.Downloading) ?? inFlight[0];
            var avgPct = inFlight.Average(d => d.ProgressPercent);
            FloatingPercent = Math.Clamp(avgPct, 0, 100);
            FloatingPercentText = Format.Percent(FloatingPercent);
            FloatingTitleText = inFlight.Count == 1
                ? primary.FileName
                : $"({inFlight.Count}) {primary.FileName}";
        }
        else
        {
            FloatingPercent = 0;
            FloatingPercentText = "0%";
        }
    }

    // ------------------------------------------------------------------ per-item actions (IDownloadActions)

    public Task StartAsync(DownloadItemViewModel item) => _engine.StartAsync(item.Id);

    public Task PauseAsync(DownloadItemViewModel item) => _engine.PauseAsync(item.Id);

    public Task CancelAsync(DownloadItemViewModel item) => _engine.CancelAsync(item.Id);

    public async Task RemoveAsync(DownloadItemViewModel item, bool deleteFile)
    {
        if (deleteFile && !_shell.Confirm(Loc.Format("confirm_delete", item.FileName))) return;
        await RemoveCoreAsync(item, deleteFile).ConfigureAwait(true);
        Refilter();
        RefreshCounts();
    }

    private async Task RemoveCoreAsync(DownloadItemViewModel item, bool deleteFile)
    {
        _removed.Add(item.Id);
        await _engine.RemoveAsync(item.Id, deleteFile).ConfigureAwait(true);
        _byId.Remove(item.Id);
        if (ReferenceEquals(_selected, item)) SelectedDownload = null;
        Downloads.Remove(item);
    }

    public void OnItemCheckedChanged(DownloadItemViewModel item)
    {
        if (!item.IsPlaylistGroup && !string.IsNullOrWhiteSpace(item.GroupId) && _playlistGroups.TryGetValue(item.GroupId!, out var groupVm))
        {
            groupVm.SyncGroupCheckedFromChildren();
        }
        RefreshSelectionCounts();
    }

    private void RefreshSelectionCounts()
    {
        CheckedCount = Downloads.Count(d => d.IsChecked);

        var visibleLeafItems = VisibleDownloads
            .SelectMany(row => row.IsPlaylistGroup ? (IEnumerable<DownloadItemViewModel>)row.Children : [row])
            .ToList();

        _suppressSelectAllCascade = true;
        try
        {
            Set(ref _isAllChecked, visibleLeafItems.Count > 0 && visibleLeafItems.All(x => x.IsChecked), nameof(IsAllChecked));
        }
        finally
        {
            _suppressSelectAllCascade = false;
        }
    }

    public void ClearSelection()
    {
        foreach (var d in Downloads) d.SetCheckedFromGroup(false);
        foreach (var g in _playlistGroups.Values) g.SyncGroupCheckedFromChildren();
        RefreshSelectionCounts();
    }

    public async Task ResumeCheckedAsync()
    {
        foreach (var d in Downloads.Where(d => d.IsChecked && d.CanStart).ToList())
            await _engine.StartAsync(d.Id).ConfigureAwait(true);
    }

    public async Task PauseCheckedAsync()
    {
        foreach (var d in Downloads.Where(d => d.IsChecked && d.CanPause).ToList())
            await _engine.PauseAsync(d.Id).ConfigureAwait(true);
    }

    public async Task CancelCheckedAsync()
    {
        foreach (var d in Downloads.Where(d => d.IsChecked && d.CanCancel).ToList())
            await _engine.CancelAsync(d.Id).ConfigureAwait(true);
    }

    public async Task RemoveCheckedAsync()
    {
        var targets = Downloads.Where(d => d.IsChecked).ToList();
        if (targets.Count == 0) return;
        foreach (var d in targets)
            await RemoveCoreAsync(d, deleteFile: false).ConfigureAwait(true);
        Refilter();
        RefreshCounts();
    }

    public async Task DeleteCheckedFilesAsync()
    {
        var targets = Downloads.Where(d => d.IsChecked).ToList();
        if (targets.Count == 0) return;
        if (!_shell.Confirm(Loc.Format("confirm_delete_many", targets.Count))) return;

        foreach (var d in targets)
            await RemoveCoreAsync(d, deleteFile: true).ConfigureAwait(true);
        Refilter();
        RefreshCounts();
    }

    public void Open(DownloadItemViewModel item)
    {
        if (item.Status == DownloadStatus.Completed && File.Exists(item.FilePath)) _shell.OpenFile(item.FilePath);
    }

    public void OpenFolder(DownloadItemViewModel item)
    {
        var folder = Path.GetDirectoryName(item.FilePath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) _shell.OpenFolder(item.Status == DownloadStatus.Completed ? item.FilePath : folder);
    }

    // ------------------------------------------------------------------ bulk actions

    public async Task PauseAllAsync()
    {
        foreach (var d in Downloads.Where(d => d.CanPause).ToList()) await _engine.PauseAsync(d.Id).ConfigureAwait(true);
    }

    public async Task ResumeAllAsync()
    {
        foreach (var d in Downloads.Where(d => d.Status == DownloadStatus.Paused).ToList()) await _engine.StartAsync(d.Id).ConfigureAwait(true);
    }

    /// <summary>
    /// Programmatic add-and-start helper (uses global DefaultConnections).
    /// </summary>
    public async Task AddAndStartAsync(string url, string? fileName = null, string? referrer = null)
    {
        var directory = _settings.DownloadDirectory;

        if (YouTubeService.IsYouTubeUrl(url) && _youtube.IsAvailable)
        {
            if (YouTubeService.IsPlaylistUrl(url))
                await AddYouTubeAsync([], [url], directory, start: true, null, wantAudio: false, null).ConfigureAwait(true);
            else
                await AddYouTubeAsync([url], [], directory, start: true, null, wantAudio: false, null).ConfigureAwait(true);
            return;
        }

        var item = await _engine.AddAsync(new AddDownloadRequest(url, directory)
        {
            FileName = fileName,
            Connections = _settings.DefaultConnections,
            Referer = referrer,
        }).ConfigureAwait(true);
        await _engine.StartAsync(item.Id).ConfigureAwait(true);
    }

    private async Task ClearCompletedAsync()
    {
        foreach (var d in Downloads.Where(d => d.Status == DownloadStatus.Completed).ToList()) await RemoveAsync(d, deleteFile: false).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ settings

    private void SaveSettings(SettingsViewModel editor)
    {
        var model = editor.ToModel();
        model.Normalize(_defaultDirectory);
        if (!Path.IsPathRooted(model.DownloadDirectory)) model.DownloadDirectory = _defaultDirectory;
        model.Save(_settingsStore);
        ApplySettings(model);
        editor.Message = Loc["set_saved"];
    }

    private void ApplySettings(AppSettings model)
    {
        _settings = model;
        Loc.Language = model.Language;
        Theme = model.Theme;
        _engine.SetDefaultConnections(model.DefaultConnections);
        _engine.SetGlobalSpeedLimit(model.GlobalSpeedLimitKBps * 1024);
        _engine.SetMaxConcurrentDownloads(model.MaxConcurrentDownloads);
        SettingsPage.Load(model);
    }
}
