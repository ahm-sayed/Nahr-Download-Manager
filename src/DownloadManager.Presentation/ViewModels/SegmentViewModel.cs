using DownloadManager.Core.Models;
using DownloadManager.Presentation.Formatting;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.Mvvm;

namespace DownloadManager.Presentation.ViewModels;

public sealed class SegmentViewModel : ObservableObject
{
    private readonly Localizer? _loc;
    private SegmentInfo _lastSeg;
    private long _lastDownloaded = -1;
    private long? _lastActualBytes;
    private long _lastLength = -1;
    private DownloadStatus _lastStatus = (DownloadStatus)(-1);
    private double _percent;
    private string _downloadedText = "";
    private string _bytesOnlyText = "0 B";
    private string _infoText = "";

    public SegmentViewModel(SegmentInfo s, Localizer? loc = null, DownloadStatus parentStatus = DownloadStatus.Downloading)
    {
        _loc = loc;
        _lastSeg = s.Clone();
        Index = s.Index + 1;
        RangeText = $"{Format.Bytes(s.Start)} – {Format.Bytes(s.End + 1)}";
        Update(s, parentStatus);
    }

    public int Index { get; }
    public string RangeText { get; }

    public double Percent
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public string DownloadedText
    {
        get => _downloadedText;
        private set => Set(ref _downloadedText, value);
    }

    public string BytesOnlyText
    {
        get => _bytesOnlyText;
        private set => Set(ref _bytesOnlyText, value);
    }

    public string InfoText
    {
        get => _infoText;
        private set => Set(ref _infoText, value);
    }

    public void RefreshLanguage(DownloadStatus parentStatus)
    {
        _lastStatus = (DownloadStatus)(-1);
        Update(_lastSeg, parentStatus);
    }

    public void Update(SegmentInfo s, DownloadStatus parentStatus = DownloadStatus.Downloading)
    {
        if (s.Downloaded == _lastDownloaded
            && s.ActualBytes == _lastActualBytes
            && s.Length == _lastLength
            && parentStatus == _lastStatus)
        {
            return;
        }

        _lastDownloaded = s.Downloaded;
        _lastActualBytes = s.ActualBytes;
        _lastLength = s.Length;
        _lastStatus = parentStatus;
        _lastSeg = s.Clone();
        Percent = s.Length > 0 ? Math.Min(100.0, s.Downloaded * 100.0 / s.Length) : 0;
        var realBytes = s.ActualBytes ?? s.Downloaded;
        BytesOnlyText = Format.DetailedBytes(realBytes);
        DownloadedText = s.ActualBytes.HasValue
            ? Format.Bytes(s.ActualBytes.Value)
            : Format.Bytes(s.Downloaded) + " / " + Format.Bytes(s.Length);

        if (_loc is null)
        {
            InfoText = s.IsComplete ? "Completed" : (realBytes > 0 ? "Receiving data…" : "Connecting…");
            return;
        }

        if (s.IsComplete || parentStatus == DownloadStatus.Completed)
            InfoText = _loc["status_Completed"];
        else if (parentStatus == DownloadStatus.Downloading)
            InfoText = realBytes > 0 ? _loc["dl_receiving_data"] : _loc["dl_connecting_srv"];
        else if (parentStatus == DownloadStatus.Probing)
            InfoText = _loc["dl_connecting_srv"];
        else if (parentStatus == DownloadStatus.Paused)
            InfoText = _loc["status_Paused"];
        else if (parentStatus == DownloadStatus.Failed)
            InfoText = _loc["status_Failed"];
        else
            InfoText = _loc["dl_waiting_conn"];
    }
}
