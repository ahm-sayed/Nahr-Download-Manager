using System.Text.RegularExpressions;

namespace DownloadManager.Core.Security;

/// <summary>
/// Sanitises header values before they are stored or logged. Sensitive header names
/// (Authorization, Cookie, Proxy-Authorization, Set-Cookie, X-Api-Key) must never be
/// persisted in plain text in the database or surfaced in the UI/log.
/// </summary>
public static class HeaderSanitiser
{
    public const string Redacted = "[redacted]";

    private static readonly Regex SensitiveHeaderRegex = new(
        @"(?i)\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns a copy of the given header values with sensitive values replaced by <see cref="Redacted"/>.
    /// Safe for persistence; never throws.
    /// </summary>
    public static Dictionary<string, string> Sanitize(Dictionary<string, string>? headers)
    {
        if (headers is null) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var kvp in headers)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            if (IsSensitive(key) || IsSensitiveValue(key, value))
                value = Redacted;
            result[key] = value;
        }
        return result;
    }

    private static bool IsSensitive(string name) =>
        SensitiveHeaderRegex.IsMatch($"{name}: {string.Empty}") ||
        IsSensitiveName(name);

    private static bool IsSensitiveName(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveValue(string key, string value) =>
        IsSensitiveName(key) && !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Redacts the secret portion of a single header line, e.g. "Authorization: Bearer xyz".
    /// Returns the original line when the header is not sensitive.
    /// </summary>
    public static string SanitizeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return line ?? string.Empty;
        var match = SensitiveHeaderRegex.Match(line);
        if (!match.Success) return line;
        return $"{match.Groups[1].Value.Trim()}={Redacted}";
    }
}
