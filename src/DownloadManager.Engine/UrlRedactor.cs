namespace DownloadManager.Engine;

/// <summary>The only way URLs may reach log output: scheme, host and file name; never query, user-info or full path.</summary>
public static class UrlRedactor
{
    public static string Redact(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<invalid-url>";
        var last = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
        return $"{uri.Scheme}://{uri.Host}/…/{last}";
    }
}
