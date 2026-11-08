namespace TurboDL.Core.Sites;

/// <summary>A config file that wasn't loaded, and why, in a sentence for the user.</summary>
public sealed record SiteConfigProblem(string File, string Reason);

/// <summary>The site configs that loaded (built-in ones, with user files in their place where the ids match), and the files that didn't.</summary>
public sealed class SiteCatalog(IReadOnlyList<SiteConfig> sites, IReadOnlyList<SiteConfigProblem> problems)
{
    public static SiteCatalog Empty { get; } = new([], []);

    /// <summary>Ordered by name.</summary>
    public IReadOnlyList<SiteConfig> Sites { get; } = [.. sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<SiteConfigProblem> Problems { get; } = problems;

    /// <summary>The config for a link's host (the most specific one when several match), or null for a host with none.</summary>
    public SiteConfig? Match(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme is not ("http" or "https")) return null;
        var host = url.IdnHost;
        SiteConfig? best = null;
        var bestLength = -1;
        foreach (var site in Sites)
        {
            var length = site.MatchLength(host);
            if (length > bestLength) (best, bestLength) = (site, length);
        }
        return best;
    }
}
