using System.Net;

namespace DownloadManager.Engine;

public static class EngineHttp
{
    /// <summary>
    /// Creates the engine's HttpClient with honest defaults:
    /// - An honest User-Agent identifying the tool (Nahr/<version>)
    /// - Accept / Accept-Language headers
    /// Cookies are off because byte ranges refer to the encoded representation and we request "identity".
    /// Automatic decompression is off for the same reason.
    /// </summary>
    public static HttpClient Create(EngineOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // Honest defaults — every request will layer on per-download overrides supplied by the user.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(options.Accept);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(options.AcceptLanguage);

        return client;
    }
}
