using System.Text.RegularExpressions;

namespace DownloadManager.Presentation.Services;

/// <summary>Finds downloadable http/https links inside arbitrary text, such as clipboard contents.</summary>
public static class UrlExtractor
{
    /// <summary>Longest URL that will be accepted; anything longer is almost always junk pasted by accident.</summary>
    private const int MaxUrlLength = 2048;

    // ASCII-only on purpose. Arabic words, punctuation and bidi marks directly touching a link end the match
    // instead of being swallowed into it, which is what happens when a URL is pasted next to Arabic text.
    // Whitespace, quotes and angle brackets all terminate a URL; control characters and DEL are never valid.
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s\u0000-\u001F\u007F-￿""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Sentence punctuation and quotes commonly glued to a URL by prose. The typographic marks matter for text
    // copied out of rich documents; they are listed here so trimming stays correct even if the pattern changes.
    private const string TrailingJunk = ".,;:!?)]}>\"'«»“”‘’";

    /// <summary>Extracts absolute http/https URLs from arbitrary text.</summary>
    /// <param name="text">Free-form text, for example the current clipboard text.</param>
    /// <returns>
    /// The URLs in order of appearance, with trailing punctuation removed and duplicates collapsed
    /// (case-insensitively, keeping the first spelling). Empty when <paramref name="text"/> is null or blank.
    /// </returns>
    public static IReadOnlyList<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in UrlPattern.Matches(text))
        {
            var candidate = TrimTrailing(match.Value);
            if (!IsAcceptable(candidate)) continue;
            if (seen.Add(candidate)) urls.Add(candidate);
        }

        return urls;
    }

    /// <summary>Removes prose punctuation that the URL pattern legitimately matched at the very end.</summary>
    private static string TrimTrailing(string url)
    {
        var end = url.Length;
        while (end > 0 && TrailingJunk.IndexOf(url[end - 1]) >= 0) end--;
        return end == url.Length ? url : url[..end];
    }

    /// <summary>Rejects over-long strings, links without a host and links carrying embedded credentials.</summary>
    private static bool IsAcceptable(string url)
    {
        if (url.Length > MaxUrlLength) return false;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;

        var authorityStart = schemeEnd + 3;
        if (authorityStart >= url.Length) return false;

        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? url[authorityStart..] : url[authorityStart..authorityEnd];

        // "user:password@host" would leak secrets and is never a download link, so drop the whole URL.
        return authority.Length > 0 && authority.IndexOf('@') < 0;
    }
}
