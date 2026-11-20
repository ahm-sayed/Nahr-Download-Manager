using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DownloadManager.Core.Models;
using DownloadManager.Engine;
using DownloadManager.Engine.Hls;
using DownloadManager.Engine.YouTube;
using DownloadManager.Presentation.Formatting;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;

namespace DownloadManager.Presentation.ViewModels;

/// <summary>
/// ViewModel for the standalone IDM-style Download Window.
/// Supports switching between Video and Audio download modes (each with its own dedicated quality list)
/// before clicking Start Download, then transitions into Live Download Progress mode.
/// </summary>
public sealed class DownloadSessionViewModel : ObservableObject
{
    private readonly Localizer _loc;
    private readonly Func<string?, string?> _pickFolder;
    private readonly Func<DownloadSessionViewModel, bool, Task<DownloadItemViewModel?>>? _commitPreDownloadAsync;
    private readonly Action<Guid, long>? _setSpeedLimit;
    private readonly PropertyChangedEventHandler _onItemPropertyChanged;
    private readonly List<QualityChoice> _videoQualities = new();
    private readonly List<QualityChoice> _audioQualities = new();

    private DownloadItemViewModel? _item;
    private string _url = "";
    private string _fileName = "";
    private string _baseTitle = "";
    private string _destinationDirectory = "";
    private string _sizeText = "";
    private string _categoryKey = Categorizer.General;
    private string _probeStatus = "";
    private string _errorMessage = "";
    private string _speedLimitKBpsText = "512";
    private int _activeProgressTab;
    private bool _isPreDownload;
    private bool _isMediaClip;
    private bool _isAudioMode;
    private bool _isProbing;
    private bool _isStarting;
    private bool _hasQualities;
    private bool _showDetails = true;
    private bool _useSpeedLimiter;
    private bool _closeOnComplete;
    private bool _openFileOnComplete;
    private bool _openFolderOnComplete;
    private bool _completionHandled;
    private QualityChoice? _selectedQuality;
    private QualityChoice? _savedVideoQuality;
    private QualityChoice? _savedAudioQuality;
    private long? _knownBytes;
    private bool _isAllPlaylistSelected = true;
    private bool _suppressPlaylistSelectAllCascade;
    private int _selectedPlaylistCount;

    public event Action? RequestClose;

    /// <summary>Creates a session in Pre-Download confirmation mode.</summary>
    public DownloadSessionViewModel(
        ExtensionDownloadRequest request,
        string defaultDirectory,
        Localizer loc,
        Func<string?, string?> pickFolder,
        Func<DownloadSessionViewModel, bool, Task<DownloadItemViewModel?>> commitAsync,
        Action<Guid, long>? setSpeedLimit = null)
    {
        _loc = loc;
        _pickFolder = pickFolder;
        _commitPreDownloadAsync = commitAsync;
        _setSpeedLimit = setSpeedLimit;
        _onItemPropertyChanged = OnBoundItemPropertyChanged;

        Request = request;
        _url = request.Url;
        _destinationDirectory = defaultDirectory;
        _isPreDownload = true;

        var isYt = YouTubeService.IsYouTubeUrl(request.Url) || string.Equals(request.MediaType, "ytdlp", StringComparison.OrdinalIgnoreCase);
        var isHls = HlsStreamService.LooksLikeHlsUrl(request.Url, request.MediaType);
        var isVideoOrAudio = isYt || isHls ||
                             request.MediaType is "video" or "audio" or "youtube" or "playlist" ||
                             HasVideoOrAudioExtension(request.Url);

        _isMediaClip = isVideoOrAudio;
        _isAudioMode = request.PreferAudio || string.Equals(request.MediaType, "audio", StringComparison.OrdinalIgnoreCase);

        var initialName = !string.IsNullOrWhiteSpace(request.Filename)
            ? request.Filename!
            : !string.IsNullOrWhiteSpace(request.PageTitle)
                ? SanitizeBaseName(request.PageTitle!) + GuessDefaultExtension(request, _isAudioMode)
                : GuessFileNameFromUrl(request.Url, request.MediaType);

        if (isHls)
        {
            initialName = Path.ChangeExtension(initialName, _isAudioMode ? ".mp3" : ".mp4");
        }

        _fileName = initialName;
        _baseTitle = Path.GetFileNameWithoutExtension(initialName);
        _categoryKey = _isAudioMode ? "Music" : (isVideoOrAudio ? "Video" : Categorizer.ForFileName(_fileName));
        _knownBytes = request.FileSize;
        _sizeText = request.FileSize is > 0 ? Format.Bytes(request.FileSize.Value) : loc["dl_probing"];

        if (isYt)
        {
            _videoQualities.AddRange(QualityChoice.CreateYouTubeVideoPresets(loc));
            _audioQualities.AddRange(QualityChoice.CreateAudioQualityOptions(loc));
            if (!string.IsNullOrWhiteSpace(request.PreferredQualityKey))
            {
                _savedVideoQuality = _videoQualities.FirstOrDefault(q =>
                    string.Equals(q.Key, request.PreferredQualityKey, StringComparison.OrdinalIgnoreCase));
                _savedAudioQuality = _audioQualities.FirstOrDefault(q =>
                    string.Equals(q.Key, request.PreferredQualityKey, StringComparison.OrdinalIgnoreCase));
            }
            SyncModeQualities();
        }
        else if (request.Qualities is { Count: > 0 })
        {
            var idx = 0;
            foreach (var q in request.Qualities)
            {
                var h = q.Height ?? HlsStreamService.TryExtractHeightFromUrl(q.Url) ?? HlsStreamService.TryExtractHeightFromUrl(q.Label);
                var label = q.Label;
                if (string.IsNullOrWhiteSpace(label) ||
                    label.StartsWith("HLS ", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(label, "Stream", StringComparison.OrdinalIgnoreCase))
                {
                    label = h is > 0
                        ? $"{HlsStreamService.FormatResolutionHead(h.Value)} ({(q.Extension ?? "mp4").ToUpperInvariant()})"
                        : (string.IsNullOrWhiteSpace(label) ? $"Video Stream {idx + 1}" : label);
                }

                _videoQualities.Add(new QualityChoice
                {
                    Key = h is > 0 ? h.Value.ToString() : $"ext:{idx}",
                    Label = label,
                    DirectMediaUrl = q.Url,
                    CompanionAudioUrl = q.AudioUrl,
                    IsHlsStream = isHls || HlsStreamService.LooksLikeHlsUrl(q.Url),
                    Extension = q.Extension ?? "mp4",
                    EstimatedBytes = q.FileSize,
                });
                idx++;
            }
            _audioQualities.AddRange(QualityChoice.CreateAudioQualityOptions(
                loc,
                directSourceUrl: request.Qualities[^1].Url,
                isHls: isHls));

            var targetVideoUrl = !string.IsNullOrWhiteSpace(request.SelectedQualityUrl) ? request.SelectedQualityUrl : request.Url;
            _savedVideoQuality =
                _videoQualities.FirstOrDefault(q => string.Equals(q.DirectMediaUrl, targetVideoUrl, StringComparison.OrdinalIgnoreCase)) ??
                (!string.IsNullOrWhiteSpace(request.PreferredQualityKey)
                    ? _videoQualities.FirstOrDefault(q =>
                        string.Equals(q.Key, request.PreferredQualityKey, StringComparison.OrdinalIgnoreCase) ||
                        q.Label.Contains(request.PreferredQualityKey + "p", StringComparison.OrdinalIgnoreCase))
                    : null) ??
                _videoQualities.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(request.PreferredQualityKey))
            {
                _savedAudioQuality = _audioQualities.FirstOrDefault(q =>
                    string.Equals(q.Key, request.PreferredQualityKey, StringComparison.OrdinalIgnoreCase));
            }
            SyncModeQualities();
        }
        else if (_isMediaClip)
        {
            var h = HlsStreamService.TryExtractHeightFromUrl(request.Url);
            var singleLabel = h is > 0
                ? $"{HlsStreamService.FormatResolutionHead(h.Value)} (MP4)"
                : "Original Video (MP4)";

            _videoQualities.Add(new QualityChoice
            {
                Key = h is > 0 ? h.Value.ToString() : "orig",
                Label = singleLabel,
                DirectMediaUrl = request.Url,
                IsHlsStream = isHls,
                Extension = "mp4",
                EstimatedBytes = request.FileSize,
            });
            _audioQualities.AddRange(QualityChoice.CreateAudioQualityOptions(
                loc,
                directSourceUrl: request.Url,
                isHls: isHls));
            if (!string.IsNullOrWhiteSpace(request.PreferredQualityKey))
            {
                _savedAudioQuality = _audioQualities.FirstOrDefault(q =>
                    string.Equals(q.Key, request.PreferredQualityKey, StringComparison.OrdinalIgnoreCase));
            }
            SyncModeQualities();
        }

        StartDownloadCommand = new AsyncRelayCommand(() => CommitAsync(startNow: true), CanCommitPreDownload);
        DownloadLaterCommand = new AsyncRelayCommand(() => CommitAsync(startNow: false), CanCommitPreDownload);
        BrowseCommand = new RelayCommand(() =>
        {
            var picked = _pickFolder(_destinationDirectory);
            if (!string.IsNullOrWhiteSpace(picked)) DestinationDirectory = picked;
        });
        CloseWindowCommand = new RelayCommand(() => RequestClose?.Invoke());
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
        ApplySpeedLimitCommand = new RelayCommand(PushSpeedLimitToEngine);
        Preset256Command = new RelayCommand(() => SetSpeedPreset(256));
        Preset512Command = new RelayCommand(() => SetSpeedPreset(512));
        Preset1024Command = new RelayCommand(() => SetSpeedPreset(1024));
        Preset2048Command = new RelayCommand(() => SetSpeedPreset(2048));
        Preset5120Command = new RelayCommand(() => SetSpeedPreset(5120));

        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(Localizer.IsRtl)) OnPropertyChanged(string.Empty);
        };
    }

    /// <summary>Creates a session directly in Live Progress mode for an existing <see cref="DownloadItemViewModel"/>.</summary>
    public DownloadSessionViewModel(
        DownloadItemViewModel item,
        Localizer loc,
        Func<string?, string?> pickFolder,
        Action<Guid, long>? setSpeedLimit = null)
    {
        _loc = loc;
        _pickFolder = pickFolder;
        _setSpeedLimit = setSpeedLimit;
        _onItemPropertyChanged = OnBoundItemPropertyChanged;
        _isPreDownload = false;

        StartDownloadCommand = new RelayCommand(() => { });
        DownloadLaterCommand = new RelayCommand(() => { });
        BrowseCommand = new RelayCommand(() => { });
        CloseWindowCommand = new RelayCommand(() => RequestClose?.Invoke());
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
        ApplySpeedLimitCommand = new RelayCommand(PushSpeedLimitToEngine);
        Preset256Command = new RelayCommand(() => SetSpeedPreset(256));
        Preset512Command = new RelayCommand(() => SetSpeedPreset(512));
        Preset1024Command = new RelayCommand(() => SetSpeedPreset(1024));
        Preset2048Command = new RelayCommand(() => SetSpeedPreset(2048));
        Preset5120Command = new RelayCommand(() => SetSpeedPreset(5120));

        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(Localizer.IsRtl)) OnPropertyChanged(string.Empty);
        };

        AttachItem(item);
    }

    public ExtensionDownloadRequest? Request { get; }
    public Task? BackgroundProbeTask { get; set; }
    public YouTubeVideoInfo? ProbedVideoInfo { get; set; }
    public YouTubePlaylistInfo? ProbedPlaylistInfo { get; set; }

    public Localizer Loc => _loc;
    public bool IsRtl => _loc.IsRtl;

    public DownloadItemViewModel? Item => _item;
    public Guid? BoundDownloadId => _item?.Id;

    public bool IsPreDownload
    {
        get => _isPreDownload;
        private set
        {
            if (Set(ref _isPreDownload, value))
            {
                OnPropertyChanged(nameof(IsProgressMode));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public bool IsProgressMode => !_isPreDownload && _item is not null;

    /// <summary>True when downloading a media clip (enables the Video vs Audio mode switcher).</summary>
    public bool IsMediaClip
    {
        get => _isMediaClip;
        set => Set(ref _isMediaClip, value);
    }

    public bool IsVideoMode
    {
        get => !_isAudioMode;
        set
        {
            if (value && _isAudioMode)
            {
                _isAudioMode = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAudioMode));
                SyncModeQualities();
            }
        }
    }

    public bool IsAudioMode
    {
        get => _isAudioMode;
        set
        {
            if (value && !_isAudioMode)
            {
                _isAudioMode = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsVideoMode));
                SyncModeQualities();
            }
        }
    }

    public string Url
    {
        get => _item?.Url ?? _url;
        set => Set(ref _url, value);
    }

    public string FileName
    {
        get => _item?.FileName ?? _fileName;
        set
        {
            if (Set(ref _fileName, value))
            {
                _categoryKey = Categorizer.ForFileName(value);
                OnPropertyChanged(nameof(CategoryText));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string DestinationDirectory
    {
        get => _destinationDirectory;
        set => Set(ref _destinationDirectory, value);
    }

    public string SizeText
    {
        get => _item?.SizeText ?? _sizeText;
        set => Set(ref _sizeText, value);
    }

    public string CategoryText => _item?.CategoryText ?? _loc["cat_" + _categoryKey];

    public bool HasQualities
    {
        get => _hasQualities;
        set => Set(ref _hasQualities, value);
    }

    public ObservableCollection<QualityChoice> Qualities { get; } = new();

    public QualityChoice? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!Set(ref _selectedQuality, value) || value is null) return;
            if (_isAudioMode) _savedAudioQuality = value;
            else _savedVideoQuality = value;
            ApplySelectedQuality(value);
        }
    }

    public bool IsProbing
    {
        get => _isProbing;
        set => Set(ref _isProbing, value);
    }

    public bool IsStarting
    {
        get => _isStarting;
        private set
        {
            if (Set(ref _isStarting, value))
            {
                (StartDownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DownloadLaterCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ActionStatusText));
            }
        }
    }

    public string ProbeStatus
    {
        get => _probeStatus;
        set
        {
            if (Set(ref _probeStatus, value)) OnPropertyChanged(nameof(ActionStatusText));
        }
    }

    public string ActionStatusText => _isStarting ? _loc["dl_preparing"] : _probeStatus;
    public bool HasActionStatus => !string.IsNullOrWhiteSpace(ActionStatusText);

    public string PreErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (Set(ref _errorMessage, value)) OnPropertyChanged(nameof(HasPreError));
        }
    }

    public bool HasPreError => !string.IsNullOrWhiteSpace(_errorMessage);

    public long? KnownBytes
    {
        get => _knownBytes;
        set => _knownBytes = value;
    }

    public bool CloseOnComplete
    {
        get => _closeOnComplete;
        set => Set(ref _closeOnComplete, value);
    }

    public bool OpenFileOnComplete
    {
        get => _openFileOnComplete;
        set => Set(ref _openFileOnComplete, value);
    }

    public bool OpenFolderOnComplete
    {
        get => _openFolderOnComplete;
        set => Set(ref _openFolderOnComplete, value);
    }

    // ---- IDM Progress Window Tabs & Details Toggle
    public bool IsStatusTab
    {
        get => _activeProgressTab == 0;
        set { if (value) SetActiveTab(0); }
    }

    public bool IsSpeedLimiterTab
    {
        get => _activeProgressTab == 1;
        set { if (value) SetActiveTab(1); }
    }

    public bool IsCompletionTab
    {
        get => _activeProgressTab == 2;
        set { if (value) SetActiveTab(2); }
    }

    private void SetActiveTab(int index)
    {
        if (_activeProgressTab == index) return;
        _activeProgressTab = index;
        OnPropertyChanged(nameof(IsStatusTab));
        OnPropertyChanged(nameof(IsSpeedLimiterTab));
        OnPropertyChanged(nameof(IsCompletionTab));
    }

    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (Set(ref _showDetails, value))
            {
                OnPropertyChanged(nameof(ShowConnectionDetails));
                OnPropertyChanged(nameof(DetailsToggleText));
            }
        }
    }

    public bool ShowConnectionDetails => _showDetails && IsActiveOrPaused;
    public string DetailsToggleText => _showDetails ? _loc["dl_hide_details"] : _loc["dl_show_details"];

    // ---- Per-Download Speed Limiter
    public bool UseSpeedLimiter
    {
        get => _useSpeedLimiter;
        set
        {
            if (Set(ref _useSpeedLimiter, value))
                PushSpeedLimitToEngine();
        }
    }

    public string SpeedLimitKBpsText
    {
        get => _speedLimitKBpsText;
        set => Set(ref _speedLimitKBpsText, value);
    }

    private void SetSpeedPreset(int kbps)
    {
        SpeedLimitKBpsText = kbps.ToString();
        if (!_useSpeedLimiter) UseSpeedLimiter = true;
        else PushSpeedLimitToEngine();
    }

    private void PushSpeedLimitToEngine()
    {
        if (_item is null || _setSpeedLimit is null) return;
        if (!_useSpeedLimiter)
        {
            _setSpeedLimit(_item.Id, 0);
            return;
        }

        if (long.TryParse(_speedLimitKBpsText.Trim(), out var kbps) && kbps > 0)
        {
            var clamped = Math.Clamp(kbps, 1, 1_000_000);
            _setSpeedLimit(_item.Id, clamped * 1024L);
        }
    }

    public bool IsCompleted => _item?.Status == DownloadStatus.Completed;
    public bool IsActiveOrPaused => _item is not null && _item.Status != DownloadStatus.Completed;

    public string WindowTitle
    {
        get
        {
            if (_isPreDownload) return $"{_loc["dl_win_title"]} — {_loc["app_name"]}";
            if (_item is null) return _loc["app_name"];
            if (_item.Status == DownloadStatus.Completed) return $"100% — {_item.FileName}";
            return $"{Format.Percent(_item.ProgressPercent)} — {_item.FileName}";
        }
    }

    public ICommand StartDownloadCommand { get; }
    public ICommand DownloadLaterCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand CloseWindowCommand { get; }
    public ICommand ToggleDetailsCommand { get; }
    public ICommand ApplySpeedLimitCommand { get; }
    public ICommand Preset256Command { get; }
    public ICommand Preset512Command { get; }
    public ICommand Preset1024Command { get; }
    public ICommand Preset2048Command { get; }
    public ICommand Preset5120Command { get; }

    // ------------------------------------------------------------------ updating from background probe

    public void ApplyYouTubeVideoProbe(YouTubeVideoInfo info)
    {
        ProbedVideoInfo = info;
        IsMediaClip = true;
        if (!string.IsNullOrWhiteSpace(info.Title))
        {
            _baseTitle = SanitizeBaseName(info.Title);
        }

        var prevVideoKey = _savedVideoQuality?.Key ?? Request?.PreferredQualityKey;
        var prevAudioKey = _savedAudioQuality?.Key ?? Request?.PreferredQualityKey;

        var videoFormats = info.QualityOptions.Where(f => f.HasVideo).ToList();
        if (videoFormats.Count == 0) videoFormats = info.Formats.Where(f => f.HasVideo).ToList();

        if (videoFormats.Count > 0)
        {
            _videoQualities.Clear();
            foreach (var fmt in videoFormats)
            {
                _videoQualities.Add(new QualityChoice
                {
                    Key = fmt.Height?.ToString() ?? fmt.FormatId,
                    Label = fmt.DisplayLabel,
                    YouTubeFormat = fmt,
                    DirectMediaUrl = fmt.DirectUrl,
                    Extension = fmt.EffectiveExtension,
                    EstimatedBytes = fmt.TotalEstimatedBytes,
                });
            }
        }

        var bestAudio = info.AudioOptions.FirstOrDefault() ?? info.Formats.FirstOrDefault(f => !f.HasVideo && f.HasAudio);
        var lowAudio = info.AudioOptions.LastOrDefault();
        _audioQualities.Clear();
        _audioQualities.AddRange(QualityChoice.CreateAudioQualityOptions(_loc, info.Duration, bestAudio, lowAudio));

        _savedVideoQuality = _videoQualities.FirstOrDefault(q => q.Key == prevVideoKey) ?? _videoQualities.FirstOrDefault();
        _savedAudioQuality = _audioQualities.FirstOrDefault(q => q.Key == prevAudioKey) ?? _audioQualities.FirstOrDefault();
        SyncModeQualities();
    }

    public ObservableCollection<PlaylistEntryItem> PlaylistEntries { get; } = new();
    public bool HasPlaylistEntries => PlaylistEntries.Count > 0;
    public int SelectedPlaylistCount => _selectedPlaylistCount;
    public string PlaylistSelectionSummaryText => _loc.Format("pl_select_summary", _selectedPlaylistCount, PlaylistEntries.Count);

    public bool IsAllPlaylistSelected
    {
        get => _isAllPlaylistSelected;
        set
        {
            if (_suppressPlaylistSelectAllCascade)
            {
                Set(ref _isAllPlaylistSelected, value);
                return;
            }
            _isAllPlaylistSelected = value;
            OnPropertyChanged();
            foreach (var entry in PlaylistEntries)
                entry.SetSelectedSilently(value);
            RefreshPlaylistSelectionState();
        }
    }

    private bool CanCommitPreDownload() => !_isStarting && (!HasPlaylistEntries || _selectedPlaylistCount > 0);

    private void RefreshPlaylistSelectionState()
    {
        _selectedPlaylistCount = PlaylistEntries.Count(e => e.IsSelected);
        _suppressPlaylistSelectAllCascade = true;
        try
        {
            Set(ref _isAllPlaylistSelected, PlaylistEntries.Count > 0 && _selectedPlaylistCount == PlaylistEntries.Count, nameof(IsAllPlaylistSelected));
        }
        finally
        {
            _suppressPlaylistSelectAllCascade = false;
        }
        OnPropertyChanged(nameof(SelectedPlaylistCount));
        OnPropertyChanged(nameof(PlaylistSelectionSummaryText));
        if (_selectedPlaylistCount > 0 && PreErrorMessage == _loc["pl_none_selected"])
            PreErrorMessage = "";
        (StartDownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadLaterCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Returns the playlist entries currently checked by the user (or all entries if the list has not loaded yet).</summary>
    public IReadOnlyList<YouTubePlaylistEntry> GetSelectedPlaylistEntries()
    {
        if (PlaylistEntries.Count > 0)
            return PlaylistEntries.Where(e => e.IsSelected).Select(e => e.Entry).ToList();
        return ProbedPlaylistInfo?.Entries ?? Array.Empty<YouTubePlaylistEntry>();
    }

    public void ApplyYouTubePlaylistProbe(YouTubePlaylistInfo playlist)
    {
        ProbedPlaylistInfo = playlist;
        IsMediaClip = true;
        if (!string.IsNullOrWhiteSpace(playlist.Title))
        {
            _baseTitle = SanitizeBaseName(playlist.Title);
            FileName = _baseTitle;
        }

        PlaylistEntries.Clear();
        var idx = 1;
        foreach (var entry in playlist.Entries)
        {
            PlaylistEntries.Add(new PlaylistEntryItem(idx++, entry, RefreshPlaylistSelectionState));
        }
        OnPropertyChanged(nameof(HasPlaylistEntries));
        RefreshPlaylistSelectionState();

        SizeText = _loc.Format("yt_playlist_found", playlist.Entries.Count);
        ProbeStatus = "";
        OnPropertyChanged(nameof(HasActionStatus));
    }

    public void ApplyHlsVariantsProbe(IReadOnlyList<(string Label, string Url, string? AudioUrl)> variants)
    {
        if (variants.Count == 0) return;

        // If the browser extension already provided multiple distinct resolution variants and probing a single URL
        // only returned 1 media playlist, preserve the richer multi-quality list from the browser!
        if (variants.Count == 1 && _videoQualities.Count > 1)
        {
            SyncModeQualities();
            return;
        }

        var explicitUrl = !string.IsNullOrWhiteSpace(Request?.SelectedQualityUrl) ? Request!.SelectedQualityUrl : null;
        var explicitKey = !string.IsNullOrWhiteSpace(Request?.PreferredQualityKey) ? Request!.PreferredQualityKey : _savedVideoQuality?.Key;
        var prevVideoUrl = explicitUrl ?? _savedVideoQuality?.DirectMediaUrl ?? Request?.Url;
        var prevHeight = (int.TryParse(explicitKey, out var parsedH) ? (int?)parsedH : null)
                      ?? HlsStreamService.TryExtractHeightFromUrl(explicitUrl)
                      ?? HlsStreamService.TryExtractHeightFromUrl(_savedVideoQuality?.Label)
                      ?? HlsStreamService.TryExtractHeightFromUrl(prevVideoUrl);
        var prevAudioKey = !string.IsNullOrWhiteSpace(Request?.PreferredQualityKey) ? Request!.PreferredQualityKey : _savedAudioQuality?.Key;

        IsMediaClip = true;
        _videoQualities.Clear();
        var idx = 0;
        foreach (var (label, variantUrl, audioUrl) in variants)
        {
            var h = HlsStreamService.TryExtractHeightFromUrl(label) ?? HlsStreamService.TryExtractHeightFromUrl(variantUrl);
            _videoQualities.Add(new QualityChoice
            {
                Key = h is > 0 ? h.Value.ToString() : $"hls:{idx}",
                Label = label,
                DirectMediaUrl = variantUrl,
                CompanionAudioUrl = audioUrl,
                IsHlsStream = true,
                Extension = "mp4",
            });
            idx++;
        }

        // For audio extraction from HLS, prefer dedicated audio rendition or lowest-bandwidth stream!
        var lightest = variants[^1];
        var audioStreamUrl = lightest.AudioUrl ?? lightest.Url;
        _audioQualities.Clear();
        _audioQualities.AddRange(QualityChoice.CreateAudioQualityOptions(
            _loc,
            directSourceUrl: audioStreamUrl,
            isHls: true));

        _savedVideoQuality =
            (explicitUrl is not null
                ? _videoQualities.FirstOrDefault(q => string.Equals(q.DirectMediaUrl, explicitUrl, StringComparison.OrdinalIgnoreCase))
                : null) ??
            (prevHeight is > 0
                ? _videoQualities.FirstOrDefault(q =>
                    q.Key == prevHeight.Value.ToString() ||
                    HlsStreamService.TryExtractHeightFromUrl(q.Label) == prevHeight.Value ||
                    HlsStreamService.TryExtractHeightFromUrl(q.DirectMediaUrl) == prevHeight.Value)
                : null) ??
            _videoQualities.FirstOrDefault(q => string.Equals(q.DirectMediaUrl, prevVideoUrl, StringComparison.OrdinalIgnoreCase)) ??
            _videoQualities.FirstOrDefault();

        _savedAudioQuality =
            _audioQualities.FirstOrDefault(q => string.Equals(q.Key, prevAudioKey, StringComparison.OrdinalIgnoreCase)) ??
            _audioQualities.FirstOrDefault();
        SyncModeQualities();
    }

    public void ApplyHttpProbe(string probedName, long? contentLength, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(Request?.Filename) && !string.IsNullOrWhiteSpace(probedName))
        {
            _baseTitle = Path.GetFileNameWithoutExtension(probedName);
            if (_isMediaClip && _isAudioMode)
            {
                var ext = _selectedQuality?.Extension ?? "mp3";
                FileName = $"{_baseTitle}.{ext}";
            }
            else
            {
                FileName = probedName;
            }
        }

        if (contentType is not null &&
            (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
             contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) &&
            !_isMediaClip)
        {
            IsMediaClip = true;
            if (_videoQualities.Count == 0)
            {
                _videoQualities.Add(new QualityChoice
                {
                    Key = "orig",
                    Label = $"Original ({Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant()})",
                    DirectMediaUrl = _url,
                    Extension = Path.GetExtension(FileName).TrimStart('.'),
                    EstimatedBytes = contentLength,
                });
            }
            if (_audioQualities.Count == 0)
            {
                _audioQualities.AddRange(QualityChoice.CreateAudioQualityOptions(_loc, directSourceUrl: _url));
            }
            SyncModeQualities();
        }

        if (contentLength is > 0)
        {
            _knownBytes = contentLength;
            if (!_isAudioMode || _selectedQuality?.EstimatedBytes is null)
                SizeText = Format.Bytes(contentLength.Value);
        }
        else if (_knownBytes is null or <= 0)
        {
            if (!_selectedQuality?.IsHlsStream == true)
                SizeText = _loc["unknown_size"];
        }
    }

    public void AttachItem(DownloadItemViewModel item)
    {
        if (_item is not null) _item.PropertyChanged -= _onItemPropertyChanged;
        _item = item;
        _completionHandled = item.Status == DownloadStatus.Completed;
        if (item.SpeedLimitBytesPerSecond > 0)
        {
            _useSpeedLimiter = true;
            _speedLimitKBpsText = Math.Max(1, item.SpeedLimitBytesPerSecond / 1024).ToString();
        }
        _item.PropertyChanged += _onItemPropertyChanged;
        IsPreDownload = false;
        OnPropertyChanged(string.Empty);
        if (_useSpeedLimiter) PushSpeedLimitToEngine();
    }

    private void SyncModeQualities()
    {
        var activeList = _isAudioMode ? _audioQualities : _videoQualities;
        var preferred = _isAudioMode ? _savedAudioQuality : _savedVideoQuality;

        Qualities.Clear();
        foreach (var q in activeList) Qualities.Add(q);

        HasQualities = Qualities.Count > 0;
        var next = (preferred is not null && Qualities.Contains(preferred) ? preferred : null) ?? Qualities.FirstOrDefault();
        if (next is not null)
        {
            if (ReferenceEquals(_selectedQuality, next)) ApplySelectedQuality(next);
            else SelectedQuality = next;
        }
    }

    private void ApplySelectedQuality(QualityChoice q)
    {
        var ext = q.Extension ?? (_isAudioMode ? "mp3" : "mp4");
        if (!string.IsNullOrWhiteSpace(_baseTitle) && ProbedPlaylistInfo is null)
        {
            FileName = $"{_baseTitle}.{ext.TrimStart('.')}";
        }
        _categoryKey = _isAudioMode ? "Music" : Categorizer.ForFileName(FileName);
        OnPropertyChanged(nameof(CategoryText));

        if (q.DirectMediaUrl is not null && (q.IsHlsStream || q.IsAudioMode || Request?.Qualities is { Count: > 0 }))
        {
            _url = q.DirectMediaUrl;
            OnPropertyChanged(nameof(Url));
        }

        if (q.EstimatedBytes is > 0)
        {
            _knownBytes = q.EstimatedBytes;
            SizeText = Format.Bytes(q.EstimatedBytes.Value);
        }
        else if (q.IsHlsStream)
        {
            var h = HlsStreamService.TryExtractHeightFromUrl(q.Label) ?? HlsStreamService.TryExtractHeightFromUrl(q.DirectMediaUrl);
            var resBadge = h is > 0 ? $"{HlsStreamService.FormatResolutionHead(h.Value)} · " : "";
            SizeText = _isAudioMode
                ? $"HLS Audio ({ext.ToUpperInvariant()})"
                : $"{resBadge}HLS Stream (MP4)";
        }
    }

    private async Task CommitAsync(bool startNow)
    {
        if (_commitPreDownloadAsync is null || _isStarting) return;
        if (HasPlaylistEntries && _selectedPlaylistCount == 0)
        {
            PreErrorMessage = _loc["pl_none_selected"];
            return;
        }
        PreErrorMessage = "";
        IsStarting = true;
        OnPropertyChanged(nameof(HasActionStatus));
        try
        {
            var created = await _commitPreDownloadAsync(this, startNow).ConfigureAwait(true);
            if (created is not null && startNow)
            {
                AttachItem(created);
            }
            else
            {
                RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            PreErrorMessage = ex.Message;
        }
        finally
        {
            IsStarting = false;
            OnPropertyChanged(nameof(HasActionStatus));
        }
    }

    private void OnBoundItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var prop = e.PropertyName;
        if (string.IsNullOrEmpty(prop)
            || prop is nameof(DownloadItemViewModel.PercentText)
            or nameof(DownloadItemViewModel.FileName)
            or nameof(DownloadItemViewModel.Status))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }

        if (string.IsNullOrEmpty(prop) || prop == nameof(DownloadItemViewModel.Status))
        {
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsActiveOrPaused));
            OnPropertyChanged(nameof(ShowConnectionDetails));

            if (!_completionHandled && _item?.Status == DownloadStatus.Completed)
            {
                _completionHandled = true;
                if (_openFileOnComplete && _item.CanOpen) _item.OpenCommand.Execute(null);
                if (_openFolderOnComplete) _item.OpenFolderCommand.Execute(null);
                if (_closeOnComplete) RequestClose?.Invoke();
            }
        }
    }

    private static bool HasVideoOrAudioExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var ext = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
        return ext is ".mp4" or ".webm" or ".mkv" or ".mov" or ".avi" or ".mp3" or ".m4a" or ".ogg" or ".wav" or ".flac";
    }

    private static string SanitizeBaseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) < 0 ? c : '_');
        }
        if (sb.Length > 180) sb.Length = 180;
        var clean = sb.ToString().Trim();
        return clean.Length > 0 ? clean : "download";
    }

    private static string GuessDefaultExtension(ExtensionDownloadRequest req, bool audioMode) =>
        audioMode ? ".mp3" : req.MediaType switch
        {
            "audio" => ".mp3",
            "youtube" or "ytdlp" or "video" or "hls" => ".mp4",
            _ => "",
        };

    private static string GuessFileNameFromUrl(string url, string? mediaType = null)
    {
        if (string.Equals(mediaType, "hls", StringComparison.OrdinalIgnoreCase))
            return "video.mp4";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var seg = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(seg) && seg != "/")
                return Uri.UnescapeDataString(seg);
        }
        return "download";
    }
}

/// <summary>One selectable video row inside the pre-download YouTube playlist picker.</summary>
public sealed class PlaylistEntryItem : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected = true;

    public PlaylistEntryItem(int index, YouTubePlaylistEntry entry, Action onSelectionChanged)
    {
        Index = index;
        Entry = entry;
        _onSelectionChanged = onSelectionChanged;
    }

    public int Index { get; }
    public string IndexText => Index.ToString("00");
    public YouTubePlaylistEntry Entry { get; }
    public string VideoId => Entry.VideoId;
    public string Title => Entry.Title;
    public string Url => Entry.Url;

    public string DurationText => Entry.Duration is { TotalSeconds: > 0 } d
        ? (d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}")
        : "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
                _onSelectionChanged();
        }
    }

    internal void SetSelectedSilently(bool value) => Set(ref _isSelected, value, nameof(IsSelected));
}
