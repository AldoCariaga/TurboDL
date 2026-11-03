using System.Text.RegularExpressions;

namespace TurboDL.Core.Catching;

/// <summary>Pulls every http(s) link out of free text, such as a list of links pasted from a web page or a chat.</summary>
public static partial class UrlExtractor
{
    /// <summary>Longer candidates are skipped.</summary>
    public const int MaxUrlLength = 4096;

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', '\'', '"', '>'];

    // Lazy, so a link ends at whitespace, at a character that can't be in a URL, or where the next http(s):// begins.
    // That last stop splits links pasted with nothing between them ("…part01.rarhttp://…part02.rar").
    [GeneratedRegex("""https?://.+?(?=https?://|[\s<>"'\[\]{}|\\^`]|$)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Candidate();

    /// <summary>
    /// Every distinct http(s) link in <paramref name="text"/>, in the order they first appear. Trailing punctuation
    /// is dropped, and so is a closing ")" with no "(" to match it, so "(https://a.com/x)" gives the bare link but
    /// "https://en.wikipedia.org/wiki/Foo_(bar)" keeps its ")".
    /// Known trade-off: a link that carries another URL in its query string ("https://a.com/go?u=http://b.com/x") is
    /// split at the inner "http://", because that same rule is what splits links pasted with no gap between them.
    /// </summary>
    public static IReadOnlyList<Uri> ExtractAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Uri>();
        foreach (Match m in Candidate().Matches(text))
        {
            var candidate = TrimEnd(m.Value);
            if (candidate.Length > MaxUrlLength) continue;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
            if (string.IsNullOrEmpty(uri.Host)) continue;
            if (seen.Add(uri.AbsoluteUri)) result.Add(uri);
        }
        return result;
    }

    private static string TrimEnd(string candidate)
    {
        while (candidate.Length > 0)
        {
            var last = candidate[^1];
            if (Array.IndexOf(TrailingPunctuation, last) >= 0)
                candidate = candidate[..^1];
            else if (last == ')' && candidate.Count(c => c == ')') > candidate.Count(c => c == '('))
                candidate = candidate[..^1];
            else
                break;
        }
        return candidate;
    }
}
