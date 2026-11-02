using System.Net;

namespace TurboDL.Core.Engine;

public static class TurboHttp
{
    /// <summary>
    /// Used only when no User-Agent was passed in. Downloads caught from the browser carry the browser's own.
    /// Deliberately not a fake Chrome string: some servers drop clients whose UA doesn't match their TLS fingerprint.
    /// </summary>
    public const string DefaultUserAgent = "TurboDL/0.1";

    /// <summary>
    /// One client for the whole app. Decompression is off so byte offsets match the file on the server,
    /// and cookies are off because they arrive as an explicit Cookie header from the browser.
    /// </summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // Stalls are detected per read in DownloadTask instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        var hasUserAgent = false;
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (string.IsNullOrWhiteSpace(name) || IsManagedHeader(name)) continue;
                if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!hasUserAgent) request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
    }

    // Headers the engine controls itself; a browser-supplied copy would break range requests.
    private static bool IsManagedHeader(string name) =>
        name.Equals("Range", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
}
