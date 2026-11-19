using System.Collections.Concurrent;

namespace DownloadManager.TestServer;

public sealed record RequestRecord(string Path, string Range, int Status, long BytesSent);

/// <summary>Counters and fault-injection knobs shared between the tests and the server handler.</summary>
public sealed class TestServerState
{
    // Knobs (public fields so they can be used with Interlocked).
    public int Version = 1;               // content seed + ETag/Last-Modified version
    public int FlakyDropsRemaining;       // "flaky": responses cut off at 40% (connection aborted)
    public int StallsRemaining;           // "stall": responses freeze at 30% until the client gives up
    public int FailuresRemaining;         // "fail": requests answered with 503 first
    public int SlowChunkDelayMs = 1;     // "slow": delay after every 16 KiB chunk

    private int _active;
    private int _maxActive;
    private long _bytesSent;
    private int _generation;

    public ConcurrentQueue<RequestRecord> Requests { get; } = new();
    /// <summary>Incremented by <see cref="Reset"/>; handlers still running from an earlier test must not touch the new test's records.</summary>
    internal int Generation => Volatile.Read(ref _generation);
    public int MaxConcurrentRequests => Volatile.Read(ref _maxActive);
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    internal void Enter()
    {
        var now = Interlocked.Increment(ref _active);
        int cur;
        while (now > (cur = Volatile.Read(ref _maxActive)))
            if (Interlocked.CompareExchange(ref _maxActive, now, cur) == cur) break;
    }

    internal void Leave() => Interlocked.Decrement(ref _active);
    internal void AddBytes(long n, int generation)
    {
        if (generation == Generation) Interlocked.Add(ref _bytesSent, n);
    }

    /// <summary>Waits until every in-flight handler has finished and recorded itself, so counters are final.</summary>
    public async Task WaitForIdleAsync(CancellationToken ct, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _active) > 0 && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10, ct);
    }

    public void Reset()
    {
        Version = 1;
        FlakyDropsRemaining = 0;
        StallsRemaining = 0;
        FailuresRemaining = 0;
        SlowChunkDelayMs = 25;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _maxActive, Volatile.Read(ref _active));
        Interlocked.Exchange(ref _bytesSent, 0);
        Requests.Clear();
    }
}