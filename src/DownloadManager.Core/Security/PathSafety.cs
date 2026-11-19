using System.Text;

namespace DownloadManager.Core.Security;

/// <summary>File-name and path hardening. Every server- or user-supplied name must pass through here.</summary>
public static class PathSafety
{
    public const int MaxFileNameLength = 180;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns a single, safe file-name component (never a path).</summary>
    public static string SanitizeFileName(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        // Keep only the last path segment: neutralises "../x", "..\x", "C:\x", "/etc/x".
        var normalized = name.Replace('\\', '/');
        var last = normalized[(normalized.LastIndexOf('/') + 1)..];

        var sb = new StringBuilder(last.Length);
        foreach (var ch in last)
            sb.Append(char.IsControl(ch) || Array.IndexOf(InvalidChars, ch) >= 0 ? '_' : ch);

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) return fallback;

        var dot = cleaned.IndexOf('.');
        var stem = (dot < 0 ? cleaned : cleaned[..dot]).TrimEnd();
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;

        if (cleaned.Length > MaxFileNameLength)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length > 20) ext = ext[..20];
            cleaned = cleaned[..(MaxFileNameLength - ext.Length)] + ext;
        }

        return cleaned;
    }

    /// <summary>
    /// Returns a path inside <paramref name="directory"/> that does not exist yet (also checks the ".part" file and
    /// any extra reservation), appending " (n)" before the extension. Prevents accidental overwrites.
    /// </summary>
    public static string GetUniqueFilePath(string directory, string fileName, Func<string, bool>? isReserved = null)
    {
        var safe = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);

        for (var i = 0; i < 100_000; i++)
        {
            var candidate = i == 0 ? safe : $"{stem} ({i}){ext}";
            var path = Path.Combine(directory, candidate);
            if (!File.Exists(path) && !File.Exists(path + ".part") && !(isReserved?.Invoke(path) ?? false))
            {
                EnsureInsideDirectory(directory, path);
                return path;
            }
        }

        throw new IOException("Could not find a free file name.");
    }

    /// <summary>Throws if <paramref name="path"/> resolves outside <paramref name="directory"/> (path traversal guard).</summary>
    public static void EnsureInsideDirectory(string directory, string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(directory).TrimEnd(sep, Path.AltDirectorySeparatorChar) + sep;
        var full = Path.GetFullPath(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, cmp))
            throw new UnauthorizedAccessException("The target path is outside the download directory.");
    }
}
