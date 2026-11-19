using System.Diagnostics;
using DownloadManager.Presentation.Services;

namespace DownloadManager.Verification;

public static class ClipboardTests
{
    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("clipboard: detects a single URL in clipboard text", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("https://example.com/file.zip");
            Assert.Equal(1, urls.Count, "should find one URL in clipboard");
            Assert.Equal("https://example.com/file.zip", urls[0], "URL should be extracted correctly");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: ignores plain text with no URLs", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("This is just plain text with no URLs");
            Assert.Equal(0, urls.Count, "no URLs should be found in plain text");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: finds three URLs on separate lines", async _ =>
        {
            var clipboardText = """
                https://example.com/file1.zip
                https://example.com/file2.pdf
                https://example.com/file3.mp3
                """;
            var urls = UrlExtractor.ExtractUrls(clipboardText);
            Assert.Equal(3, urls.Count, "should find three URLs in clipboard");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: finds two URLs in mixed content", async _ =>
        {
            var clipboardText = """
                Check out this file: https://example.com/file.zip
                And this one: https://example.com/another.pdf
                Just some text here
                """;
            var urls = UrlExtractor.ExtractUrls(clipboardText);
            Assert.Equal(2, urls.Count, "should find two URLs in mixed content");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: strips trailing sentence punctuation", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("see https://example.com/file.zip.");
            Assert.Equal(1, urls.Count, "should find one URL");
            Assert.Equal("https://example.com/file.zip", urls[0], "trailing punctuation should be stripped");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: de-duplicates the same URL twice", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("https://example.com/x.zip https://example.com/x.zip");
            Assert.Equal(1, urls.Count, "duplicate URL should be collapsed");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: rejects ftp and file URLs", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("ftp://example.com/x.zip file:///etc/passwd");
            Assert.Equal(0, urls.Count, "ftp and file URLs should be rejected");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: rejects a URL with embedded credentials", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("https://user:pw@example.com/x.zip");
            Assert.Equal(0, urls.Count, "URL with credentials should be rejected");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: returns empty for null and whitespace input", async _ =>
        {
            Assert.Equal(0, UrlExtractor.ExtractUrls(null).Count, "null should yield no URLs");
            Assert.Equal(0, UrlExtractor.ExtractUrls("   \t\n").Count, "whitespace should yield no URLs");
            await Task.CompletedTask;
        });

        yield return new TestCase("clipboard: handles a URL surrounded by Arabic text", async _ =>
        {
            var urls = UrlExtractor.ExtractUrls("حمّل هذا الملف: https://example.com/file.zip من هنا");
            Assert.Equal(1, urls.Count, "should find one URL in Arabic text");
            Assert.Equal("https://example.com/file.zip", urls[0], "URL should be extracted correctly");
            await Task.CompletedTask;
        });
    }
}