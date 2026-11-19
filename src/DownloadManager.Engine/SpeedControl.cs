using System.Diagnostics;

namespace DownloadManager.Engine;

/// <summary>
/// Token bucket with debt accounting. <see cref="Reserve"/> never blocks; it returns how long the caller must wait,
/// so several workers sharing one limiter queue up fairly and the aggregate rate converges on the limit.
/// </summary>
public sealed class TokenBucketSpeedLimiter
{
    private readonly object _gate = new();
    private long _rate;
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();

    public TokenBucketSpeedLimiter(long bytesPerSecond = 0) => _rate = Math.Max(0, bytesPerSecond);

    /// <summary>0 = unlimited. Can be changed while downloads are running.</summary>
    public long BytesPerSecond
    {
        get { lock (_gate) return _rate; }
        set
        {
            lock (_gate)
            {
                _rate = Math.Max(0, value);
                _tokens = 0;
                _last = Stopwatch.GetTimestamp();
            }
        }
    }

    public TimeSpan Reserve(int bytes)
    {
        lock (_gate)
        {
            if (_rate <= 0) return TimeSpan.Zero;
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            var capacity = Math.Max(_rate / 4.0, 1);          // at most a quarter second of burst
            _tokens = Math.Min(capacity, _tokens + elapsed * _rate);
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }
}

/// <summary>Combines the global limiter and the per-download limiter: the slower of the two wins.</summary>
public sealed class BandwidthThrottle(TokenBucketSpeedLimiter? global, TokenBucketSpeedLimiter? perDownload)
{
    public ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var a = global?.Reserve(bytes) ?? TimeSpan.Zero;
        var b = perDownload?.Reserve(bytes) ?? TimeSpan.Zero;
        var delay = a > b ? a : b;
        return delay <= TimeSpan.Zero ? ValueTask.CompletedTask : new ValueTask(Task.Delay(delay, ct));
    }
}
