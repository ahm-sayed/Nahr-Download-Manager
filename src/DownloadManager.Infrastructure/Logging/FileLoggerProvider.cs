using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Infrastructure.Logging;

/// <summary>
/// Daily rolling log file. As defence in depth, every line is scrubbed of URL query strings, user-info and
/// Authorization/Cookie style header values before it is written - on top of the engine's own redaction.
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information, int retainDays = 14)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        DeleteOldFiles(retainDays);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    public static string Scrub(string text)
    {
        text = HeaderValue().Replace(text, "$1: [redacted]");
        text = UserInfo().Replace(text, "$1[redacted]@");
        text = QueryString().Replace(text, "$1?[redacted]");
        return text;
    }

    [GeneratedRegex(@"(?i)\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*[^\r\n]+")]
    private static partial Regex HeaderValue();

    [GeneratedRegex(@"(?i)(https?://)[^/\s@]+@")]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"(https?://[^\s?""']+)\?[^\s""']*")]
    private static partial Regex QueryString();

    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var tag = level switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
        };
        var now = DateTime.UtcNow;
        var line = $"{now:yyyy-MM-ddTHH:mm:ss.fffZ} [{tag}] {category}: {message}";
        if (ex is not null) line += Environment.NewLine + "    " + ex.GetType().Name + ": " + ex.Message;
        line = Scrub(line) + Environment.NewLine;

        try
        {
            lock (_gate) File.AppendAllText(Path.Combine(_directory, $"nahr-{now:yyyyMMdd}.log"), line);
        }
        catch (IOException) { /* logging must never crash the app */ }
        catch (UnauthorizedAccessException) { }
    }

    private void DeleteOldFiles(int retainDays)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "nahr-*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-retainDays)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
