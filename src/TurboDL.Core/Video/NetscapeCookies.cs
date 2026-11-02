using System.Text;

namespace TurboDL.Core.Video;

/// <summary>
/// Turns a browser Cookie header into the Netscape cookies.txt format yt-dlp reads with <c>--cookies</c>.
/// Cookie values never go on a command line, where other processes and logs could see them.
/// </summary>
public static class NetscapeCookies
{
    /// <summary>
    /// The cookie domain for a page: the host without a leading "www.", with subdomains included,
    /// so cookies from www.youtube.com also reach m.youtube.com and music.youtube.com.
    /// </summary>
    public static string DomainFor(Uri pageUrl)
    {
        var host = pageUrl.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4) host = host[4..];
        return "." + host;
    }

    /// <summary>
    /// Builds the file text. Pairs that would break the tab-separated format (tabs or line breaks) are dropped.
    /// Cookies are marked as expiring in a day: yt-dlp may skip session cookies (expiry 0) when it loads the file.
    /// </summary>
    public static string Format(string cookieHeader, Uri pageUrl, DateTimeOffset now)
    {
        var domain = DomainFor(pageUrl);
        var expires = now.AddDays(1).ToUnixTimeSeconds();
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var pair in cookieHeader.Split(';'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (name.Length == 0 || name.AsSpan().IndexOfAny("\t\r\n") >= 0 || value.AsSpan().IndexOfAny("\t\r\n") >= 0) continue;
            // domain, include subdomains, path, secure only, expiry, name, value
            sb.Append(domain).Append("\tTRUE\t/\tFALSE\t").Append(expires).Append('\t')
              .Append(name).Append('\t').Append(value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Writes the cookies to a new temporary file (in the user's own temp folder) and returns its path.</summary>
    public static string WriteTempFile(string cookieHeader, Uri pageUrl)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TurboDL");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, Format(cookieHeader, pageUrl, DateTimeOffset.UtcNow), new UTF8Encoding(false));
        return path;
    }

    public static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still held by a process that's being torn down; the temp folder is the user's own.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
