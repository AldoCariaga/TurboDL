using System.Text.RegularExpressions;

namespace TurboDL.Core.Sites;

/// <summary>What one step of a site config does.</summary>
public enum SiteAction
{
    /// <summary>Waits until an element is visible, hidden or attached.</summary>
    WaitFor,

    /// <summary>Waits until a countdown element is hidden or shows 0.</summary>
    Countdown,

    /// <summary>When a CAPTCHA is shown and unsolved, brings the browser on screen and waits for the user to solve it.</summary>
    Captcha,

    /// <summary>A real mouse click on an element.</summary>
    Click,

    /// <summary>A fixed pause.</summary>
    Wait,
}

/// <summary>The state a <see cref="SiteAction.WaitFor"/> step waits for.</summary>
public enum WaitState
{
    Visible,
    Hidden,
    Attached,
}

/// <summary>What happens to tabs the host's page opens.</summary>
public enum PopupHandling
{
    /// <summary>A tab that isn't the file or the host's own page is closed as an ad (the general behaviour).</summary>
    CloseAds,

    /// <summary>Every tab stays open.</summary>
    Keep,
}

/// <summary>What happens when the host's tab is sent to another site (a click that an ad script turned into a redirect).</summary>
public enum OffSiteHandling
{
    /// <summary>Nothing: the page may move to another site.</summary>
    Allow,

    /// <summary>The tab goes back to the host (or reloads the link), and the steps start again.</summary>
    GoBack,
}

/// <summary>What happens when a config's steps have all run and the file hasn't come yet.</summary>
public enum StepsEnd
{
    /// <summary>The general autopilot takes over the page.</summary>
    Generic,

    /// <summary>The browser comes on screen for the user to finish.</summary>
    ShowBrowser,
}

/// <summary>Where a site config came from.</summary>
public enum SiteSource
{
    /// <summary>Ships with TurboDL.</summary>
    BuiltIn,

    /// <summary>A JSON file in the user's sites folder.</summary>
    User,
}

/// <summary>
/// One step of a site config. <see cref="Selector"/> is a CSS or Playwright selector (WaitFor, Countdown, Click);
/// <see cref="Seconds"/> is the pause of a Wait step. A step that isn't <see cref="Optional"/> and doesn't finish within
/// <see cref="TimeoutSeconds"/> ends the config's steps.
/// </summary>
public sealed record SiteStep(SiteAction Action, string? Selector, WaitState State, int TimeoutSeconds, double Seconds, bool Optional)
{
    /// <summary>The step for a status line: <c>clicking "#download"</c>, <c>waiting 2 s</c>.</summary>
    public string Describe() => Action switch
    {
        SiteAction.WaitFor => $"waiting for \"{Selector}\" to be {State.ToString().ToLowerInvariant()}",
        SiteAction.Countdown => $"waiting out the countdown \"{Selector}\"",
        SiteAction.Captcha => "checking for a CAPTCHA",
        SiteAction.Click => $"clicking \"{Selector}\"",
        _ => $"waiting {Seconds:0.#} s",
    };
}

/// <summary>
/// How to get through one file host's page, declared in JSON (see <see cref="SiteConfigLoader"/>). There is deliberately
/// no script: a config can only wait, click and hand over to the user, so a file someone drops in can't run code.
/// </summary>
public sealed record SiteConfig
{
    /// <summary>Unique, lower case; a user file with a built-in's id replaces it.</summary>
    public required string Id { get; init; }

    /// <summary>The name shown in Settings and in the download's log.</summary>
    public required string Name { get; init; }

    /// <summary>Lower-case host names; each also matches its subdomains.</summary>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>Links on these hosts go straight to the browser, without trying them over HTTP first.</summary>
    public bool BrowserFirst { get; init; }

    public required IReadOnlyList<SiteStep> Steps { get; init; }

    /// <summary>A response whose URL matches counts as the file (with or without an attachment header). Null: none.</summary>
    public Regex? CaptureUrl { get; init; }

    /// <summary>
    /// Where the file may come from besides <see cref="Hosts"/> (each with its subdomains). Empty: anywhere. A download
    /// from any other host (an ad's) is cancelled and ignored.
    /// </summary>
    public IReadOnlyList<string> FileHosts { get; init; } = [];

    /// <summary>False: any successful non-page response counts as the file, not only one marked as an attachment.</summary>
    public bool RequireAttachment { get; init; } = true;

    public PopupHandling Popups { get; init; }

    /// <summary>What happens when the host's own tab ends up on another site.</summary>
    public OffSiteHandling OffSiteRedirects { get; init; }

    public StepsEnd WhenStepsEnd { get; init; }

    public SiteSource Source { get; init; }

    /// <summary>The file the config was read from: a path for a user file, the resource name for a built-in one.</summary>
    public required string Origin { get; init; }

    /// <summary>True for a user file that replaced the built-in config with the same id.</summary>
    public bool ReplacesBuiltIn { get; init; }

    /// <summary>
    /// How closely <paramref name="host"/> matches: the length of the longest config host it is or is a subdomain of, or
    /// -1. Label by label, never by substring: "evilhost.com" doesn't match "host.com".
    /// </summary>
    public int MatchLength(string host) => MatchLength(host, Hosts);

    /// <summary>True when the file may come from <paramref name="url"/>'s host: any host when <see cref="FileHosts"/> is empty.</summary>
    public bool IsFileHost(string url)
    {
        if (FileHosts.Count == 0) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return MatchLength(uri.IdnHost, FileHosts) >= 0 || MatchLength(uri.IdnHost) >= 0;
    }

    private static int MatchLength(string host, IEnumerable<string> hosts)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        var best = -1;
        foreach (var h in hosts)
        {
            if (host == h || (host.Length > h.Length && host.EndsWith(h, StringComparison.Ordinal) && host[^(h.Length + 1)] == '.'))
                best = Math.Max(best, h.Length);
        }
        return best;
    }

    /// <summary>True when <paramref name="url"/> matches <see cref="CaptureUrl"/> (false when there's none, or it takes too long).</summary>
    public bool IsFileUrl(string url)
    {
        if (CaptureUrl is null) return false;
        try { return CaptureUrl.IsMatch(url); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
