using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Input;
using DownloadManager.Core.Models;
using DownloadManager.Presentation.Formatting;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;

namespace DownloadManager.Presentation.ViewModels;

/// <summary>What the per-row buttons call; implemented by <see cref="MainViewModel"/>.</summary>
public interface IDownloadActions
{
    Task StartAsync(DownloadItemViewModel item);
    Task PauseAsync(DownloadItemViewModel item);
    Task CancelAsync(DownloadItemViewModel item);
    Task RemoveAsync(DownloadItemViewModel item, bool deleteFile);
    void Open(DownloadItemViewModel item);
    void OpenFolder(DownloadItemViewModel item);
    void ShowProgressWindow(DownloadItemViewModel item);
    void OnItemCheckedChanged(DownloadItemViewModel item);
}

public sealed class DownloadItemViewModel : ObservableObject
{
    private readonly Localizer _loc;
    private readonly IDownloadActions _actions;
    private DownloadStatus _status;
    private long _downloaded;
    private long? _total;
    private double _speed;
    private TimeSpan? _eta;
    private double _playlistProgress;
    private int _completedChildrenCount;
    private bool _isChecked;
    private bool _suppressChildCheckCascade;
    private bool _isExpanded;

    /// <summary>Creates a single-file download row ViewModel.</summary>
    public DownloadItemViewModel(DownloadItem snapshot, Localizer loc, IDownloadActions actions)
    {
        _loc = loc;
        _actions = actions;
        Id = snapshot.Id;
        IsPlaylistGroup = false;

        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                foreach (var seg in Segments) seg.RefreshLanguage(_status);
                OnPropertyChanged(string.Empty);
            }
        };

        StartCommand = new AsyncRelayCommand(() => actions.StartAsync(this), () => CanStart);
        PauseCommand = new AsyncRelayCommand(() => actions.PauseAsync(this), () => CanPause);
        CancelCommand = new AsyncRelayCommand(() => actions.CancelAsync(this), () => CanCancel);
        RemoveCommand = new AsyncRelayCommand(() => actions.RemoveAsync(this, deleteFile: false));
        DeleteFileCommand = new AsyncRelayCommand(() => actions.RemoveAsync(this, deleteFile: true), () => CanOpen);
        OpenCommand = new RelayCommand(() => actions.Open(this), () => CanOpen);
        OpenFolderCommand = new RelayCommand(() => actions.OpenFolder(this));
        OpenProgressWindowCommand = new RelayCommand(() => actions.ShowProgressWindow(this));
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);

        Apply(snapshot);
    }

    /// <summary>Creates an expandable Playlist container row ViewModel grouping nested video downloads.</summary>
    public DownloadItemViewModel(string groupId, string groupTitle, Localizer loc, IDownloadActions actions)
    {
        _loc = loc;
        _actions = actions;
        Id = Guid.NewGuid();
        IsPlaylistGroup = true;
        GroupId = groupId;
        GroupTitle = groupTitle;
        FileName = groupTitle;
        CategoryKey = "Video";
        CreatedAt = DateTimeOffset.UtcNow;

        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]") OnPropertyChanged(string.Empty);
        };

        StartCommand = new AsyncRelayCommand(async () =>
        {
            foreach (var c in Children.Where(x => x.CanStart).ToList())
                await actions.StartAsync(c).ConfigureAwait(true);
        }, () => CanStart);

        PauseCommand = new AsyncRelayCommand(async () =>
        {
            foreach (var c in Children.Where(x => x.CanPause).ToList())
                await actions.PauseAsync(c).ConfigureAwait(true);
        }, () => CanPause);

        CancelCommand = new AsyncRelayCommand(async () =>
        {
            foreach (var c in Children.Where(x => x.CanCancel).ToList())
                await actions.CancelAsync(c).ConfigureAwait(true);
        }, () => CanCancel);

        RemoveCommand = new AsyncRelayCommand(async () =>
        {
            foreach (var c in Children.ToList())
                await actions.RemoveAsync(c, deleteFile: false).ConfigureAwait(true);
        });

        DeleteFileCommand = new AsyncRelayCommand(async () =>
        {
            foreach (var c in Children.ToList())
                await actions.RemoveAsync(c, deleteFile: true).ConfigureAwait(true);
        }, () => Children.Any(c => c.CanOpen));

        OpenCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenFolderCommand = new RelayCommand(() =>
        {
            var first = Children.FirstOrDefault();
            if (first is not null) actions.OpenFolder(first);
        });
        OpenProgressWindowCommand = new RelayCommand(() =>
        {
            var active = Children.FirstOrDefault(c => c.IsDownloading) ?? Children.FirstOrDefault();
            if (active is not null) actions.ShowProgressWindow(active);
        });
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public Guid Id { get; }
    public bool IsPlaylistGroup { get; }
    public bool IsSingleItem => !IsPlaylistGroup;
    public string? GroupId { get; private set; }
    public string? GroupTitle { get; private set; }
    public ObservableCollection<DownloadItemViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    public string ExpandGlyph => _isExpanded ? "\uE70D" : "\uE76C";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value)) return;
            if (IsPlaylistGroup && !_suppressChildCheckCascade)
            {
                foreach (var child in Children)
                    child.SetCheckedFromGroup(value);
            }
            _actions.OnItemCheckedChanged(this);
        }
    }

    internal void SetCheckedFromGroup(bool value) => Set(ref _isChecked, value, nameof(IsChecked));

    internal void SyncGroupCheckedFromChildren()
    {
        if (!IsPlaylistGroup) return;
        _suppressChildCheckCascade = true;
        try
        {
            Set(ref _isChecked, Children.Count > 0 && Children.All(c => c.IsChecked), nameof(IsChecked));
        }
        finally
        {
            _suppressChildCheckCascade = false;
        }
    }

    public string FileName { get; private set; } = "";
    public string Url { get; private set; } = "";
    public string FilePath { get; private set; } = "";
    public bool NeedsHtmlWarning { get; private set; }
    public string ErrorMessage { get; private set; } = "";
    public HttpStatusCode? StatusCode { get; private set; }
    public string CategoryKey { get; private set; } = Categorizer.General;
    public int Connections { get; private set; }
    public long SpeedLimitBytesPerSecond { get; private set; }
    public bool SupportsRanges { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DownloadStatus Status => _status;
    public ObservableCollection<SegmentViewModel> Segments { get; } = new();

    internal long DownloadedBytesValue => _downloaded;
    internal long? TotalBytesValue => _total;

    // ---- display (compact table & details)
    public string StatusText => _loc["status_" + _status];
    public string LiveStatusDetailText => _status switch
    {
        DownloadStatus.Downloading => _loc["dl_receiving_data"],
        DownloadStatus.Probing => _loc["dl_connecting_srv"],
        _ => StatusText,
    };

    public string CategoryText => _loc["cat_" + CategoryKey];
    public string SizeText => IsPlaylistGroup
        ? _loc.Format("pl_items_count", Children.Count)
        : (_total is { } t ? Format.Bytes(t) : _loc["unknown_size"]);

    public string PlaylistCountBadgeText => _loc.Format("pl_items_count", Children.Count);

    public string CompactSizeText
    {
        get
        {
            if (IsPlaylistGroup)
                return _downloaded > 0
                    ? $"{_loc.Format("pl_progress_summary", _completedChildrenCount, Children.Count)} · {Format.Bytes(_downloaded)}"
                    : _loc.Format("pl_progress_summary", _completedChildrenCount, Children.Count);

            if (_status == DownloadStatus.Completed) return Format.Bytes(_total ?? _downloaded);
            if (_total is > 0)
                return _downloaded > 0 ? $"{Format.Bytes(_downloaded)} / {Format.Bytes(_total.Value)}" : Format.Bytes(_total.Value);
            return _downloaded > 0 ? Format.Bytes(_downloaded) : _loc["unknown_size"];
        }
    }

    public string RangesText => _loc[SupportsRanges ? "det_yes" : "det_no"];
    public bool HasError => _status == DownloadStatus.Failed && !string.IsNullOrEmpty(ErrorMessage);
    public bool IsDownloading => _status == DownloadStatus.Downloading;
    public bool ShowStatusBadge => !IsDownloading;
    public bool IsIndeterminate => !IsPlaylistGroup && _status == DownloadStatus.Downloading && _total is null;
    public string SpeedText => IsDownloading ? Format.Speed(_speed) : "";
    public string EtaText => IsDownloading && !IsPlaylistGroup ? Format.Eta(_eta) : "";
    public double Speed => IsDownloading ? _speed : 0;

    // ---- IDM-style high-precision progress window telemetry
    public string DetailedDownloadedText
    {
        get
        {
            if (_status == DownloadStatus.Completed)
                return $"{Format.DetailedBytes(_total ?? _downloaded)} ( 100 % )";
            if (_total is > 0)
                return $"{Format.DetailedBytes(_downloaded)} ( {Format.DetailedPercent(ProgressPercent)} )";
            return Format.DetailedBytes(_downloaded);
        }
    }

    public string DetailedSpeedText => IsDownloading ? Format.DetailedSpeed(_speed) : "—";
    public string DetailedEtaText => IsDownloading ? Format.DetailedEta(_eta) : "—";

    public double ProgressPercent => IsPlaylistGroup
        ? _playlistProgress
        : (_status == DownloadStatus.Completed ? 100 : _total is > 0 ? Math.Min(100.0, _downloaded * 100.0 / _total.Value) : 0);

    public string PercentText => Format.Percent(ProgressPercent);

    public string ProgressText
    {
        get
        {
            if (IsPlaylistGroup) return CompactSizeText;
            if (_status == DownloadStatus.Completed) return Format.Bytes(_total ?? _downloaded);
            if (_total is > 0) return $"{Format.Percent(ProgressPercent)} · {Format.Bytes(_downloaded)} / {Format.Bytes(_total.Value)}";
            return Format.Bytes(_downloaded);
        }
    }

    // ---- what the buttons may do
    public bool CanStart => IsPlaylistGroup
        ? Children.Any(c => c.CanStart)
        : _status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled;

    public bool CanPause => IsPlaylistGroup
        ? Children.Any(c => c.CanPause)
        : _status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading;

    public bool CanCancel => IsPlaylistGroup
        ? Children.Any(c => c.CanCancel)
        : _status is not (DownloadStatus.Completed or DownloadStatus.Cancelled);

    public bool CanOpen => !IsPlaylistGroup && _status == DownloadStatus.Completed;
    public bool ShowStart => CanStart;
    public bool ShowPause => CanPause;

    public string PrimaryActionText => _loc[_status switch
    {
        DownloadStatus.Failed => "act_retry",
        DownloadStatus.Paused => "act_resume",
        _ => "act_start",
    }];

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DeleteFileCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenProgressWindowCommand { get; }
    public ICommand ToggleExpandCommand { get; }

    /// <summary>Updates playlist header state from its nested <see cref="Children"/>.</summary>
    public void RefreshPlaylistAggregate(string? latestTitle = null)
    {
        if (!IsPlaylistGroup) return;
        if (!string.IsNullOrWhiteSpace(latestTitle))
        {
            GroupTitle = latestTitle;
            FileName = latestTitle!;
        }

        if (Children.Count == 0)
        {
            _status = DownloadStatus.Completed;
            _downloaded = 0;
            _total = 0;
            _speed = 0;
            _playlistProgress = 0;
            _completedChildrenCount = 0;
            OnPropertyChanged(string.Empty);
            RaiseCommandStates();
            return;
        }

        CreatedAt = Children.Max(c => c.CreatedAt);
        FilePath = Children[0].FilePath;
        Url = Children[0].Url;
        CategoryKey = Children.All(c => c.CategoryKey == "Music") ? "Music" : "Video";

        _downloaded = Children.Sum(c => c.DownloadedBytesValue);
        _speed = Children.Sum(c => c.Speed);
        _completedChildrenCount = Children.Count(c => c.Status == DownloadStatus.Completed);
        _playlistProgress = Children.Average(c => c.ProgressPercent);

        if (Children.Any(c => c.Status == DownloadStatus.Downloading)) _status = DownloadStatus.Downloading;
        else if (Children.Any(c => c.Status == DownloadStatus.Probing)) _status = DownloadStatus.Probing;
        else if (Children.Any(c => c.Status == DownloadStatus.Queued)) _status = DownloadStatus.Queued;
        else if (Children.Any(c => c.Status == DownloadStatus.Paused)) _status = DownloadStatus.Paused;
        else if (_completedChildrenCount == Children.Count) _status = DownloadStatus.Completed;
        else if (Children.Any(c => c.Status == DownloadStatus.Failed)) _status = DownloadStatus.Failed;
        else _status = DownloadStatus.Cancelled;

        SyncGroupCheckedFromChildren();
        OnPropertyChanged(string.Empty);
        RaiseCommandStates();
    }

    /// <summary>Applies an authoritative snapshot from the engine (status change, add, recovery, details refresh).</summary>
    public void Apply(DownloadItem s)
    {
        _status = s.Status;
        _downloaded = s.DownloadedBytes;
        _total = s.TotalBytes;
        if (_status != DownloadStatus.Downloading) { _speed = 0; _eta = null; }
        FileName = s.FileName;
        Url = s.Url;
        FilePath = s.FilePath;
        GroupId = s.GroupId;
        GroupTitle = s.GroupTitle;
        CategoryKey = string.IsNullOrEmpty(s.Category) ? Categorizer.General : s.Category;
        ErrorMessage = s.ErrorMessage ?? "";
        StatusCode = s.StatusCode;
        Connections = s.Connections;
        SpeedLimitBytesPerSecond = s.SpeedLimitBytesPerSecond;
        SupportsRanges = s.SupportsRanges;
        NeedsHtmlWarning = s.NeedsHtmlWarning ?? false;
        CreatedAt = s.CreatedAt;
        SyncSegments(s.Segments);
        OnPropertyChanged(string.Empty);
        RaiseCommandStates();
    }

    /// <summary>Applies a live progress tick (bytes, speed, ETA) without touching status.</summary>
    public void ApplyProgress(DownloadProgress p)
    {
        if (p.Status != _status) return;

        var prevDownloaded = _downloaded;
        var prevTotal = _total;
        var prevSpeed = _speed;
        var prevEta = _eta;
        var prevPercentText = PercentText;

        _downloaded = p.DownloadedBytes;
        if (p.TotalBytes is > 0) _total = p.TotalBytes;
        _speed = p.BytesPerSecond;
        _eta = p.EstimatedTimeRemaining;

        var totalChanged = _total != prevTotal;
        var bytesChanged = _downloaded != prevDownloaded || totalChanged;
        if (bytesChanged)
        {
            OnPropertyChanged(nameof(ProgressPercent));
            if (PercentText != prevPercentText) OnPropertyChanged(nameof(PercentText));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(CompactSizeText));
            OnPropertyChanged(nameof(DetailedDownloadedText));
        }

        if (totalChanged)
        {
            OnPropertyChanged(nameof(SizeText));
            if (prevTotal is null != _total is null)
                OnPropertyChanged(nameof(IsIndeterminate));
        }

        if (Math.Abs(_speed - prevSpeed) >= 1.0 || (_speed == 0 && prevSpeed != 0))
        {
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(DetailedSpeedText));
            OnPropertyChanged(nameof(Speed));
        }

        if (_eta != prevEta)
        {
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(DetailedEtaText));
        }
    }

    public void UpdateSegments(IReadOnlyList<SegmentInfo> segments) => SyncSegments(segments);

    private void SyncSegments(IReadOnlyList<SegmentInfo> segments)
    {
        if (Segments.Count != segments.Count)
        {
            Segments.Clear();
            foreach (var s in segments) Segments.Add(new SegmentViewModel(s, _loc, _status));
            return;
        }
        for (var i = 0; i < segments.Count; i++) Segments[i].Update(segments[i], _status);
    }

    private void RaiseCommandStates()
    {
        foreach (var c in new[] { StartCommand, PauseCommand, CancelCommand, DeleteFileCommand })
            (c as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
