namespace TurboDL.Core.Catching;

/// <summary>Decides whether copied text is a single download link worth offering.</summary>
public static class LinkMatcher
{
    /// <summary>
    /// Returns the link when <paramref name="text"/> is exactly one http(s) URL whose path ends in one of
    /// <paramref name="extensions"/>; otherwise null.
    /// </summary>
    public static Uri? Match(string? text, IReadOnlyCollection<string> extensions)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 4096 || trimmed.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var ext = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)).TrimStart('.');
        return ext.Length > 0 && extensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? uri : null;
    }
}
