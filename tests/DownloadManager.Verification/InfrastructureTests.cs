using DownloadManager.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Verification;

public static class InfrastructureTests
{
    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("logging: file logger writes lines and scrubs secrets", env =>
        {
            var dir = Path.Combine(env.Dir, "logs");
            using (var provider = new FileLoggerProvider(dir))
            {
                var log = provider.CreateLogger("Test");
                log.LogInformation("fetching https://user:hunter2@example.com/a/b.zip?token=SECRET123&sig=abc done");
                log.LogWarning("Cookie: session=TOPSECRET; Authorization: Bearer XYZ");
                log.LogDebug("debug is below the default level and must not appear");
                log.LogError(new IOException("disk full"), "write failed");
            }
            var text = string.Concat(Directory.GetFiles(dir, "nahr-*.log").Select(File.ReadAllText));
            Assert.True(text.Contains("[INF] Test: fetching https://[redacted]@example.com/a/b.zip?[redacted] done"), $"url scrubbing failed: {text}");
            foreach (var secret in new[] { "hunter2", "SECRET123", "TOPSECRET", "Bearer", "XYZ" })
                Assert.True(!text.Contains(secret), $"'{secret}' leaked into the log");
            Assert.True(!text.Contains("debug is below"), "debug lines are filtered");
            Assert.True(text.Contains("[ERR] Test: write failed") && text.Contains("IOException: disk full"), "errors include the exception");
            return Task.CompletedTask;
        });

        yield return new TestCase("logging: wired exactly as the App does (LoggerFactory + provider + engine logger)", env =>
        {
            var dir = Path.Combine(env.Dir, "logs2");
            using (var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new FileLoggerProvider(dir))))
            {
                var engineLog = factory.CreateLogger<DownloadManager.Engine.DownloadEngine>();
                engineLog.LogInformation("Completed {Url}: {Bytes} bytes", DownloadManager.Engine.UrlRedactor.Redact("https://h.example/a/b/f.zip?token=SECRET"), 42);
            }
            var text = string.Concat(Directory.GetFiles(dir, "nahr-*.log").Select(File.ReadAllText));
            Assert.True(text.Contains("Completed https://h.example/…/f.zip: 42 bytes") && !text.Contains("SECRET"), $"log text: {text}");
            return Task.CompletedTask;
        });
    }
}
