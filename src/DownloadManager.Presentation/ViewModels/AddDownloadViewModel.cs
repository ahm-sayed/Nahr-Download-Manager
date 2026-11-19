using System.Collections.ObjectModel;
using System.Windows.Input;
using DownloadManager.Engine.YouTube;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;

namespace DownloadManager.Presentation.ViewModels;

/// <summary>Represents a selectable video or audio quality choice in Add or Start Download dialogs.</summary>
public sealed class QualityChoice
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public YouTubeFormat? YouTubeFormat { get; init; }
    public string? DirectMediaUrl { get; init; }
    public string? CompanionAudioUrl { get; init; }
    public bool IsHlsStream { get; init; }
    public bool IsAudioMode { get; init; }
    public int AudioBitrateKbps { get; init; }
    public string? Extension { get; init; }
    public long? EstimatedBytes { get; init; }

    public override string ToString() => Label;

    public static IReadOnlyList<QualityChoice> CreateYouTubeVideoPresets(Localizer loc) =>
    [
        new() { Key = "best", Label = loc["yt_best_quality"], Extension = "mp4" },
        new() { Key = "1080", Label = loc["yt_q_1080"], Extension = "mp4" },
        new() { Key = "720", Label = loc["yt_q_720"], Extension = "mp4" },
        new() { Key = "480", Label = loc["yt_q_480"], Extension = "mp4" },
        new() { Key = "360", Label = loc["yt_q_360"], Extension = "mp4" },
    ];

    public static IReadOnlyList<QualityChoice> CreateAudioQualityOptions(
        Localizer loc,
        TimeSpan? duration = null,
        YouTubeFormat? bestNativeAudio = null,
        YouTubeFormat? lowNativeAudio = null,
        string? directSourceUrl = null,
        bool isHls = false)
    {
        long? EstimateForBitrate(int kbps) =>
            duration is { TotalSeconds: > 1 } d ? (long)(d.TotalSeconds * (kbps * 1000.0 / 8.0)) : null;

        string WithSize(string baseLabel, long? bytes)
        {
            if (bytes is not > 0) return baseLabel;
            var mb = bytes.Value / (1024.0 * 1024.0);
            return $"{baseLabel} · {(mb >= 10 ? $"{mb:F0} MB" : $"{mb:F1} MB")}";
        }

        var est320 = EstimateForBitrate(320);
        var est192 = EstimateForBitrate(192);
        var est128 = EstimateForBitrate(128);
        var estOrig = bestNativeAudio?.FileSizeApprox ?? EstimateForBitrate(160);
        var estLow = lowNativeAudio?.FileSizeApprox ?? EstimateForBitrate(64);

        var list = new List<QualityChoice>
        {
            new()
            {
                Key = "mp3:320",
                Label = WithSize(loc["aud_mp3_320"], est320),
                IsAudioMode = true,
                AudioBitrateKbps = 320,
                Extension = "mp3",
                EstimatedBytes = est320,
                YouTubeFormat = bestNativeAudio,
                DirectMediaUrl = bestNativeAudio?.DirectUrl ?? directSourceUrl,
                IsHlsStream = isHls,
            },
            new()
            {
                Key = "mp3:192",
                Label = WithSize(loc["aud_mp3_192"], est192),
                IsAudioMode = true,
                AudioBitrateKbps = 192,
                Extension = "mp3",
                EstimatedBytes = est192,
                YouTubeFormat = bestNativeAudio,
                DirectMediaUrl = bestNativeAudio?.DirectUrl ?? directSourceUrl,
                IsHlsStream = isHls,
            },
            new()
            {
                Key = "mp3:128",
                Label = WithSize(loc["aud_mp3_128"], est128),
                IsAudioMode = true,
                AudioBitrateKbps = 128,
                Extension = "mp3",
                EstimatedBytes = est128,
                YouTubeFormat = bestNativeAudio,
                DirectMediaUrl = bestNativeAudio?.DirectUrl ?? directSourceUrl,
                IsHlsStream = isHls,
            },
            new()
            {
                Key = "m4a:orig",
                Label = WithSize(loc["aud_m4a_high"], estOrig),
                IsAudioMode = true,
                AudioBitrateKbps = 0, // stream copy when already AAC m4a, or 192k AAC
                Extension = "m4a",
                EstimatedBytes = estOrig,
                YouTubeFormat = bestNativeAudio,
                DirectMediaUrl = bestNativeAudio?.DirectUrl ?? directSourceUrl,
                IsHlsStream = isHls,
            },
            new()
            {
                Key = "m4a:64",
                Label = WithSize(loc["aud_m4a_low"], estLow),
                IsAudioMode = true,
                AudioBitrateKbps = 64,
                Extension = "m4a",
                EstimatedBytes = estLow,
                YouTubeFormat = lowNativeAudio ?? bestNativeAudio,
                DirectMediaUrl = (lowNativeAudio ?? bestNativeAudio)?.DirectUrl ?? directSourceUrl,
                IsHlsStream = isHls,
            },
        };

        return list;
    }
}

/// <summary>State of the "Add downloads" dialog. Supports pasting many links at once and Video vs Audio quality selection.</summary>
public sealed class AddDownloadViewModel : ObservableObject
{
    private readonly Action<string>? _onUrlsChanged;
    private readonly List<QualityChoice> _videoChoices = new();
    private readonly List<QualityChoice> _audioChoices = new();
    private string _urlsText = "";
    private string _destination;
    private int _connections;
    private bool _startImmediately = true;
    private bool _hasYouTubeUrl;
    private bool _isAudioMode;
    private bool _isFetchingFormats;
    private QualityChoice? _selectedQuality;
    private QualityChoice? _savedVideoChoice;
    private QualityChoice? _savedAudioChoice;
    private string _message = "";
    private string? _referer;
    private string? _userAgent;
    private string? _accept;
    private string? _acceptLanguage;
    private string? _extraHeadersText;

    public AddDownloadViewModel(
        string destination,
        int connections,
        Func<Task> confirm,
        Action cancel,
        Func<string?> browse,
        Localizer? loc = null,
        Action<string>? onUrlsChanged = null)
    {
        _destination = destination;
        _connections = connections;
        _onUrlsChanged = onUrlsChanged;
        ConfirmCommand = new AsyncRelayCommand(confirm);
        CancelCommand = new RelayCommand(cancel);
        BrowseCommand = new RelayCommand(() =>
        {
            var picked = browse();
            if (!string.IsNullOrWhiteSpace(picked)) DestinationDirectory = picked;
        });

        if (loc is not null)
        {
            _videoChoices.AddRange(QualityChoice.CreateYouTubeVideoPresets(loc));
            _audioChoices.AddRange(QualityChoice.CreateAudioQualityOptions(loc));
            RefreshActiveQualities();
        }
    }

    public ObservableCollection<QualityChoice> Qualities { get; } = new();

    public QualityChoice? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!Set(ref _selectedQuality, value) || value is null) return;
            if (_isAudioMode) _savedAudioChoice = value;
            else _savedVideoChoice = value;
        }
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
                RefreshActiveQualities();
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
                RefreshActiveQualities();
            }
        }
    }

    public void SetProbedChoices(IEnumerable<QualityChoice> videoChoices, IEnumerable<QualityChoice> audioChoices)
    {
        var prevVideoKey = _savedVideoChoice?.Key;
        var prevAudioKey = _savedAudioChoice?.Key;

        _videoChoices.Clear();
        _videoChoices.AddRange(videoChoices);
        _audioChoices.Clear();
        _audioChoices.AddRange(audioChoices);

        _savedVideoChoice = _videoChoices.FirstOrDefault(x => x.Key == prevVideoKey) ?? _videoChoices.FirstOrDefault();
        _savedAudioChoice = _audioChoices.FirstOrDefault(x => x.Key == prevAudioKey) ?? _audioChoices.FirstOrDefault();
        RefreshActiveQualities();
    }

    private void RefreshActiveQualities()
    {
        var source = _isAudioMode ? _audioChoices : _videoChoices;
        var preferred = _isAudioMode ? _savedAudioChoice : _savedVideoChoice;

        Qualities.Clear();
        foreach (var q in source) Qualities.Add(q);
        SelectedQuality = (preferred is not null && Qualities.Contains(preferred) ? preferred : null) ?? Qualities.FirstOrDefault();
    }

    public bool HasYouTubeUrl
    {
        get => _hasYouTubeUrl;
        set => Set(ref _hasYouTubeUrl, value);
    }

    public bool IsFetchingFormats
    {
        get => _isFetchingFormats;
        set => Set(ref _isFetchingFormats, value);
    }

    public YouTubeVideoInfo? CachedVideoInfo { get; set; }

    public string UrlsText
    {
        get => _urlsText;
        set
        {
            if (!Set(ref _urlsText, value)) return;
            var urls = ParseUrls(value, out _);
            HasYouTubeUrl = urls.Any(YouTubeService.IsYouTubeUrl);
            _onUrlsChanged?.Invoke(value);
        }
    }

    public string DestinationDirectory { get => _destination; set => Set(ref _destination, value); }
    public int Connections { get => _connections; set => Set(ref _connections, Math.Clamp(value, 1, 32)); }
    public bool StartImmediately { get => _startImmediately; set => Set(ref _startImmediately, value); }
    public string? Referer { get => _referer; set => Set(ref _referer, value); }
    public string? UserAgent { get => _userAgent; set => Set(ref _userAgent, value); }
    public string? Accept { get => _accept; set => Set(ref _accept, value); }
    public string? AcceptLanguage { get => _acceptLanguage; set => Set(ref _acceptLanguage, value); }
    public string? ExtraHeadersText { get => _extraHeadersText; set => Set(ref _extraHeadersText, value); }

    public string Message
    {
        get => _message;
        set
        {
            if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseCommand { get; }

    /// <summary>Extracts distinct, valid http(s) links from free text (one per line, or space separated).</summary>
    public static List<string> ParseUrls(string? text, out int skipped)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        skipped = 0;
        foreach (var token in (text ?? "").Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(token, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo))
            {
                if (seen.Add(uri.AbsoluteUri)) result.Add(uri.AbsoluteUri);
            }
            else
            {
                skipped++;
            }
        }
        return result;
    }
}
