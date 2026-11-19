using System.Diagnostics;

namespace DownloadManager.Engine;

/// <summary>Speed from bytes actually written: a sliding window for the live speed plus a run-wide average.</summary>
public sealed class SpeedMeter(TimeSpan? window = null)
{
    private readonly object _gate = new();
    private readonly Queue<(long Ts, long Bytes)> _samples = new();
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(3);
    private long _startTs = Stopwatch.GetTimestamp();
    private long _total;
    private long _windowBytes;

    public void Start()
    {
        lock (_gate)
        {
            _samples.Clear();
            _total = 0;
            _windowBytes = 0;
            _startTs = Stopwatch.GetTimestamp();
        }
    }

    public void Add(long bytes)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, bytes));
            _windowBytes += bytes;
            _total += bytes;
            Prune(now);
        }
    }

    public double CurrentBytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                Prune(now);
                var elapsed = Math.Min(_window.TotalSeconds, Stopwatch.GetElapsedTime(_startTs, now).TotalSeconds);
                return elapsed < 0.05 ? 0 : _windowBytes / elapsed;
            }
        }
    }

    public double AverageBytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                var elapsed = Stopwatch.GetElapsedTime(_startTs).TotalSeconds;
                return elapsed < 0.05 ? 0 : _total / elapsed;
            }
        }
    }

    public static TimeSpan? EstimateRemaining(long remainingBytes, double bytesPerSecond) =>
        bytesPerSecond < 1 || remainingBytes < 0 ? null : TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);

    private void Prune(long now)
    {
        while (_samples.Count > 0 && Stopwatch.GetElapsedTime(_samples.Peek().Ts, now) > _window)
            _windowBytes -= _samples.Dequeue().Bytes;
    }
}
