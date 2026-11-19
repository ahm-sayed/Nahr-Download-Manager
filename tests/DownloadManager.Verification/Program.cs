using System.Diagnostics;
using DownloadManager.TestServer;
using DownloadManager.Verification;

// Usage: dotnet run --project tests/DownloadManager.Verification [-- <name filter>]
var filter = args.FirstOrDefault();
var tests = UnitTests.All()
    .Concat(DataTests.All())
    .Concat(InfrastructureTests.All())
    .Concat(PresentationTests.All())
    .Concat(XamlTests.All())
    .Concat(EngineTests.All())
    .Concat(QueueTests.All())
    .Concat(WiringTests.All())
    .Concat(ClipboardTests.All())
    .Concat(YouTubeAndExtensionTests.All())
    .Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .ToList();

var root = Path.Combine(Path.GetTempPath(), "nahr-verification-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
await using var server = await TestServerHost.StartAsync();
Console.WriteLine($"Test server on {server.BaseUri}, {tests.Count} test(s)\n");

int passed = 0, failed = 0;
var total = Stopwatch.StartNew();
foreach (var test in tests)
{
    server.State.Reset();
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Combine(dir, "dl"));
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var env = new TestEnv { Server = server, Dir = Path.Combine(dir, "dl"), StorePath = Path.Combine(dir, "store.json"), Ct = cts.Token };
    var sw = Stopwatch.StartNew();
    try
    {
        await test.Run(env);
        passed++;
        Console.WriteLine($"  PASS  {test.Name}  ({sw.Elapsed.TotalSeconds:0.00}s)");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL  {test.Name}  ({sw.Elapsed.TotalSeconds:0.00}s)");
        Console.WriteLine($"        {(ex is AssertionException ? ex.Message : ex.ToString())}");
        foreach (var r in server.State.Requests.Take(40))
            Console.WriteLine($"          server saw: {r.Path} Range='{r.Range}' -> {r.Status}, {r.BytesSent} bytes");
    }
    finally
    {
        await env.DisposeAsync();
    }
}

try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
Console.WriteLine($"\n{passed} passed, {failed} failed, {total.Elapsed.TotalSeconds:0.0}s total");
return failed == 0 ? 0 : 1;
