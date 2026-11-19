namespace DownloadManager.Engine;

/// <summary>Async gate limiting how many downloads run at once; the limit can change at runtime.</summary>
internal sealed class ConcurrencyGate(int limit)
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private int _limit = Math.Max(1, limit);
    private int _active;

    public Task WaitAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_active < _limit && _waiters.Count == 0)
            {
                _active++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = _waiters.AddLast(tcs);
            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    lock (_lock)
                    {
                        if (node.List is null) return;       // already granted
                        _waiters.Remove(node);
                        tcs.TrySetCanceled(ct);
                    }
                });
            }
            return tcs.Task;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_active <= 0) throw new InvalidOperationException("ConcurrencyGate.Release called without a matching WaitAsync.");
            _active--;
            Pump();
        }
    }

    public void SetLimit(int newLimit)
    {
        lock (_lock)
        {
            _limit = Math.Max(1, newLimit);
            Pump();
        }
    }

    private void Pump()
    {
        while (_active < _limit && _waiters.First is { } node)
        {
            _waiters.RemoveFirst();
            if (node.Value.TrySetResult()) _active++;
        }
    }
}
