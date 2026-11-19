using System.Net;

namespace DownloadManager.Core.Abstractions;

/// <summary>Base type for permanent download failures reported to the user.</summary>
public class DownloadException : Exception
{
    public DownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class InvalidDownloadUrlException(string message) : DownloadException(message);

/// <summary>Local file-system failure (disk full, access denied...). Never retried blindly.</summary>
public sealed class DownloadFileException(string message, Exception? inner = null) : DownloadException(message, inner);

/// <summary>The finished file failed a size/consistency check. The file is never published.</summary>
public sealed class DownloadIntegrityException(string message) : DownloadException(message);

/// <summary>Server answered with a non-success HTTP status.</summary>
public sealed class DownloadHttpException : DownloadException
{
    public DownloadHttpException(HttpStatusCode status, TimeSpan? retryAfter = null)
        : base($"The server responded with HTTP {(int)status} ({status}).")
    {
        StatusCode = status;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public bool IsTransient => (int)StatusCode is 408 or 425 or 429 or >= 500;
}

/// <summary>The server does not (correctly) implement the HTTP Range request we sent.</summary>
public sealed class RangeNotHonoredException(string message) : DownloadException(message);

/// <summary>ETag / Last-Modified / length no longer match what we started with.</summary>
public sealed class RemoteFileChangedException(string message) : DownloadException(message);
