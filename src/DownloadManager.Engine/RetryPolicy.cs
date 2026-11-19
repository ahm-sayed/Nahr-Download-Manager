using System.Net.Security;
using System.Security.Authentication;
using DownloadManager.Core.Abstractions;

namespace DownloadManager.Engine;

/// <summary>Exponential backoff with jitter. The failure counter resets whenever a failed attempt still made progress.</summary>
public sealed class RetryPolicy(int maxRetries, TimeSpan baseDelay, TimeSpan maxDelay, double jitter = 0.2)
{
    public int MaxRetries { get; } = maxRetries;

    public static RetryPolicy From(EngineOptions o) => new(o.MaxRetries, o.BaseRetryDelay, o.MaxRetryDelay, o.RetryJitter);

    /// <param name="failures">1 for the first failure, 2 for the second, ...</param>
    public TimeSpan GetDelay(int failures, Exception? cause = null)
    {
        var ms = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, failures - 1)), maxDelay.TotalMilliseconds);
        if (jitter > 0) ms *= 1 + (Random.Shared.NextDouble() * 2 - 1) * jitter;
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (cause is DownloadHttpException { RetryAfter: { } retryAfter })
        {
            var capped = retryAfter > maxDelay ? maxDelay : retryAfter;
            if (capped > delay) delay = capped;
        }
        return delay;
    }

    public static bool IsTransient(Exception ex) => ex switch
    {
        DownloadHttpException h => h.IsTransient,
        DownloadException => false,                                    // permanent, includes our own give-up
        HttpRequestException { InnerException: AuthenticationException } => false, // TLS validation failure: never retry
        HttpRequestException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => true,                            // caller filters real user cancellation first
        _ => false,
    };

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct,
        Action<int, TimeSpan, Exception>? onRetry = null,
        Func<long>? progress = null)
    {
        var failures = 0;
        while (true)
        {
            var before = progress?.Invoke() ?? 0;
            try
            {
                await operation(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (progress is not null && progress() > before) failures = 0;
                failures++;
                if (failures > MaxRetries)
                    throw new DownloadException($"Giving up after {MaxRetries} retries: {ex.Message}", ex);

                var delay = GetDelay(failures, ex);
                onRetry?.Invoke(failures, delay, ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        Action<int, TimeSpan, Exception>? onRetry = null)
    {
        T result = default!;
        // Explicit delegate type: a lambda returning Task<T> would otherwise bind back to this generic overload.
        Func<CancellationToken, Task> step = async t => { result = await operation(t).ConfigureAwait(false); };
        await ExecuteAsync(step, ct, onRetry).ConfigureAwait(false);
        return result;
    }
}
