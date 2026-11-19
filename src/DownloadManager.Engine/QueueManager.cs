using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;

namespace DownloadManager.Engine;

/// <summary>
/// A fair, FIFO-by-default scheduler for download starts. Explicit priority reorders only waiting items;
/// scheduled items become eligible at their requested time. It reserves queue capacity before the engine's
/// ConcurrencyGate, and enforces optional per-category limits.
/// </summary>
internal sealed class QueueManager : IDownloadQueue, IDisposable
{
    private sealed class Waiter(DownloadItem item, long sequence)
    {
        public DownloadItem Item { get; } = item;
        public long Sequence { get; } = sequence;
        public int Priority;
        public DateTimeOffset? ScheduledStart;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public LinkedListNode<Waiter>? Node;
    }

    private sealed class Lease(QueueManager owner, string category) : IDisposable
    {
        private QueueManager? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(category);
    }

    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _pending = new();
    private readonly Dictionary<string, int> _categoryActive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _categoryLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _initialConcurrency;
    private Timer? _timer;
    private long _sequence;
    private int _maxConcurrent;
    private int _active;
    private bool _paused;
    private bool _disposed;

    public QueueManager(int maxConcurrent)
    {
        _initialConcurrency = Math.Max(1, maxConcurrent);
        _maxConcurrent = _initialConcurrency;
    }

    public int PendingCount { get { lock (_sync) return _pending.Count; } }
    public int ActiveCount { get { lock (_sync) return _active; } }


    public Task<IDisposable> WaitAsync(DownloadItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ct.IsCancellationRequested) return Task.FromCanceled<IDisposable>(ct);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var waiter = new Waiter(item, _sequence++);
            waiter.Node = _pending.AddLast(waiter);
            if (ct.CanBeCanceled)
                waiter.Registration = ct.Register(() => Cancel(waiter, ct));
            PumpLocked();
            return waiter.Completion.Task;
        }
    }

    public void SetPriority(Guid itemId, int priority)
    {
        lock (_sync)
        {
            var waiter = _pending.FirstOrDefault(w => w.Item.Id == itemId);
            if (waiter is null) return;
            waiter.Priority = priority;
            PumpLocked();
        }
    }

    public void SetScheduledStart(Guid itemId, DateTimeOffset? startAt)
    {
        lock (_sync)
        {
            var waiter = _pending.FirstOrDefault(w => w.Item.Id == itemId);
            if (waiter is null) return;
            waiter.ScheduledStart = startAt?.ToUniversalTime();
            PumpLocked();
        }
    }

    public void SetCategoryLimit(string category, int? limit)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Category must not be empty.", nameof(category));
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Category limit must be positive or null.");
        lock (_sync)
        {
            if (limit is null) _categoryLimits.Remove(category);
            else _categoryLimits[category] = limit.Value;
            PumpLocked();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _paused = paused;
            if (!paused) PumpLocked();
        }
    }

    public void SetMaxConcurrent(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_sync)
        {
            _maxConcurrent = count;
            PumpLocked();
        }
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        lock (_sync)
        {
            if (waiter.Node?.List != _pending) return;
            _pending.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(ct);
            PumpLocked();
        }
    }

    private void PumpLocked()
    {
        if (_disposed || _paused) return;
        var now = DateTimeOffset.UtcNow;
        while (_active < _maxConcurrent)
        {
            Waiter? chosen = null;
            foreach (var candidate in _pending)
            {
                if (candidate.ScheduledStart is { } start && start > now) continue;
                var candidateCategory = CategoryOf(candidate.Item);
                if (_categoryLimits.TryGetValue(candidateCategory, out var limit) && _categoryActive.GetValueOrDefault(candidateCategory) >= limit) continue;
                if (chosen is null || candidate.Priority > chosen.Priority ||
                    candidate.Priority == chosen.Priority && candidate.Sequence < chosen.Sequence)
                    chosen = candidate;
            }

            if (chosen is null) break;
            _pending.Remove(chosen.Node!);
            chosen.Node = null;
            chosen.Registration.Unregister();
            var chosenCategory = CategoryOf(chosen.Item);
            _active++;
            _categoryActive[chosenCategory] = _categoryActive.GetValueOrDefault(chosenCategory) + 1;
            chosen.Completion.TrySetResult(new Lease(this, chosenCategory));
        }

        ScheduleNextLocked(now);
    }

    private void ScheduleNextLocked(DateTimeOffset now)
    {
        var next = _pending
            .Where(w => w.ScheduledStart is { } t && t > now)
            .Select(w => w.ScheduledStart!.Value)
            .DefaultIfEmpty()
            .Min();
        if (next == default)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
        var delay = next - now;
        var ms = (int)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 1, int.MaxValue);
        _timer ??= new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(ms, Timeout.Infinite);
    }

    private void OnTimer()
    {
        lock (_sync) PumpLocked();
    }

    private void Release(string category)
    {
        lock (_sync)
        {
            if (_active <= 0) throw new InvalidOperationException("Queue lease released more than once.");
            _active--;
            var count = _categoryActive.GetValueOrDefault(category) - 1;
            if (count <= 0) _categoryActive.Remove(category);
            else _categoryActive[category] = count;
            PumpLocked();
        }
    }

    private static string CategoryOf(DownloadItem item) => string.IsNullOrWhiteSpace(item.Category) ? "General" : item.Category;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            foreach (var waiter in _pending)
            {
                waiter.Registration.Unregister();
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(QueueManager)));
            }
            _pending.Clear();
        }
    }
}
