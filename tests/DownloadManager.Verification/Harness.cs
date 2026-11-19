using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;
using DownloadManager.Engine;
using DownloadManager.TestServer;

namespace DownloadManager.Verification;

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void NotNull(object? obj, string message)
    {
        if (obj is null) throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: expected <{expected}> but was <{actual}>");
    }

    public static async Task ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        catch (Exception ex) { throw new AssertionException($"{what}: expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertionException($"{what}: expected {typeof(T).Name} but nothing was thrown");
    }

    /// <summary>Byte-for-byte comparison with the deterministic source content, plus "no leftover .part file".</summary>
    public static void FileMatches(DownloadItem item, long size, int seed = 1)
    {
        True(item.Status == DownloadStatus.Completed, $"status should be Completed but was {item.Status} ({item.ErrorMessage})");
        True(File.Exists(item.FilePath), $"final file missing: {item.FilePath}");
        True(!File.Exists(item.FilePath + ".part"), "a .part file was left behind");
        var mismatch = TestData.FindFirstMismatch(item.FilePath, size, seed);
        True(mismatch < 0, $"downloaded file differs from the source at offset {mismatch} (size on disk {new FileInfo(item.FilePath).Length}, expected {size})");
    }
}

public sealed record TestCase(string Name, Func<TestEnv, Task> Run);

public sealed class TestEnv
{
    private readonly List<DownloadEngine> _engines = new();

    public required TestServerHost Server { get; init; }
    public required string Dir { get; init; }
    public required string StorePath { get; init; }
    public required CancellationToken Ct { get; init; }

    public static EngineOptions FastOptions() => new()
    {
        MinSegmentSizeBytes = 64 * 1024,
        PersistInterval = TimeSpan.FromMilliseconds(100),
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        BaseRetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        RetryJitter = 0,
        ReadTimeout = TimeSpan.FromSeconds(5),
        ResponseHeadersTimeout = TimeSpan.FromSeconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxRetries = 6,
        MaxConcurrentDownloads = 3,
    };

    public IDownloadStore NewStore() => new JsonFileDownloadStore(StorePath);

    public DownloadEngine NewEngine(IDownloadStore? store = null, Action<EngineOptions>? tweak = null)
    {
        var options = FastOptions();
        tweak?.Invoke(options);
        var engine = new DownloadEngine(store ?? NewStore(), options);
        _engines.Add(engine);
        return engine;
    }

    public async Task<DownloadItem> DownloadAsync(DownloadEngine engine, Uri url, int connections = 8, long speedLimit = 0)
    {
        var item = await engine.AddAsync(new AddDownloadRequest(url.AbsoluteUri, Dir) { Connections = connections, SpeedLimitBytesPerSecond = speedLimit }, Ct);
        await engine.StartAsync(item.Id, Ct);
        await engine.WaitAsync(item.Id, Ct);
        await Server.State.WaitForIdleAsync(Ct);
        return engine.Get(item.Id)!;
    }

    public async Task WaitForFractionAsync(DownloadEngine engine, Guid id, double fraction, long total)
    {
        while (engine.Get(id)!.DownloadedBytes < total * fraction)
            await Task.Delay(20, Ct);
    }

    public async Task DisposeAsync()
    {
        foreach (var e in _engines) await e.DisposeAsync();
    }
}

/// <summary>Simple durable store used by the tests: one JSON file, atomic replace on every save.</summary>
public sealed class JsonFileDownloadStore(string path) : IDownloadStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();

    public Task SaveAsync(DownloadItem snapshot, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var all = Load();
            all[snapshot.Id] = snapshot;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all.Values.ToList(), Json));
            File.Move(tmp, path, overwrite: true);
        }
        return Task.CompletedTask;
    }

    public Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Load().GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<DownloadItem>>(Load().Values.ToList());
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(id)) File.WriteAllText(path, JsonSerializer.Serialize(all.Values.ToList(), Json));
        }
        return Task.CompletedTask;
    }

    private Dictionary<Guid, DownloadItem> Load() =>
        File.Exists(path)
            ? (JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(path), Json) ?? new()).ToDictionary(i => i.Id)
            : new Dictionary<Guid, DownloadItem>();
}

/// <summary>Wraps a store and, once frozen, silently drops every write: simulates the process being killed.</summary>
public sealed class FreezableStore(IDownloadStore inner) : IDownloadStore
{
    private volatile bool _frozen;
    public void Freeze() => _frozen = true;

    public Task SaveAsync(DownloadItem snapshot, CancellationToken ct = default) => _frozen ? Task.CompletedTask : inner.SaveAsync(snapshot, ct);
    public Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
    public Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _frozen ? Task.CompletedTask : inner.DeleteAsync(id, ct);
}
