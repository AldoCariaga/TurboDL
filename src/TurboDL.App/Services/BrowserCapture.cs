using Microsoft.Playwright;
using TurboDL.Core.Browser;
using TurboDL.Core.Sites;
using TurboDL.Core.Video;

namespace TurboDL.App.Services;

/// <summary>What a host page handed over once the user clicked through it: the direct file link and the session that opens it.</summary>
public sealed record CapturedSession(string Url, string UserAgent, string Referer, string CookieHeader)
{
    /// <summary>The request headers TurboDL sends with the link, like a download caught from the browser extension.</summary>
    public Dictionary<string, string> Headers() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cookie"] = CookieHeader,
        ["Referer"] = Referer,
        ["User-Agent"] = UserAgent,
    };
}

/// <summary>
/// Opens a real Chrome (or Edge) window on a file host's page and clicks through it: a host with a site config gets its
/// steps (<see cref="SiteStepRunner"/>); any other gets the general autopilot (<see cref="GenericAutopilot"/>), which
/// waits out countdowns and clicks the download button it finds by its text. Ad pop-ups are closed, and CAPTCHAs and
/// sign-ins are left to the user. The window starts off-screen and comes on screen only when the user is needed: a CAPTCHA, a
/// page that stops moving on, the user picking it on the taskbar, or <see cref="ShowAsync"/> (the row's Show browser). As
/// soon as the host sends the file (a browser download, or any response marked as an attachment), the link, cookies, user
/// agent and referring page are taken and the browser is closed; TurboDL then downloads the file itself. One browser at a
/// time (the caller sees to it). Playwright's driver comes from <see cref="PlaywrightTools"/>.
/// </summary>
public sealed class BrowserCapture(PlaywrightTools tools)
{
    /// <summary>How long a pop-up gets to turn out to be the file, or the host's own page, before it's closed as an ad.</summary>
    private static readonly TimeSpan PopupGrace = TimeSpan.FromSeconds(3);

    private readonly Lock _lock = new();
    private BrowserWindows? _open;

    /// <summary>True until the browser helper has been downloaded.</summary>
    public bool NeedsSetup => !tools.IsInstalled;

    /// <summary>Brings the open browser on screen, if there is one and it's hidden (the row's Show browser).</summary>
    public Task ShowAsync()
    {
        BrowserWindows? open;
        lock (_lock) open = _open;
        return open?.ShowAllAsync("Showing the browser.") ?? Task.CompletedTask;
    }

    /// <summary>Where the file was found, and the page it came from.</summary>
    private sealed record Hit(string Url, IPage? Page, IDownload? Download, string? Referer);

    /// <summary>
    /// Waits until the host sends a file from any tab. Null when the user closes the browser first.
    /// <paramref name="visible"/> shows the browser from the start (the user asked for it); otherwise it starts hidden.
    /// <paramref name="status"/> hears what the page is doing (each change once), for the download's log.
    /// Cancelling <paramref name="ct"/> (Pause) closes the browser and throws OperationCanceledException.
    /// <paramref name="site"/> is the link's host's config; null (no config) uses the general autopilot.
    /// </summary>
    public async Task<CapturedSession?> CaptureAsync(string url, IProgress<ToolSetupProgress>? setup = null,
        IProgress<string>? status = null, CancellationToken ct = default, bool visible = false, SiteConfig? site = null)
    {
        await tools.EnsureAsync(setup, ct);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", tools.Directory);

        using var playwright = await Playwright.CreateAsync();
        ct.ThrowIfCancellationRequested();
        var userWindow = visible ? 0 : BrowserWindows.UserWindow();
        await using var browser = await LaunchAsync(playwright, visible);
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        var done = new TaskCompletionSource<Hit?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var report = new StatusReporter(status);
        var windows = new BrowserWindows(context, report, shown: visible);

        void Watch(IPage page)
        {
            page.Download += (_, download) =>
            {
                if (site is not null && !site.IsFileHost(download.Url))
                {
                    // An ad that starts a download of its own: not the file. The browser mustn't keep it either.
                    report.Say($"Ignored a download from {HostOf(download.Url)}: it isn't one of {site.Name}'s file hosts.");
                    _ = download.CancelAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return;
                }
                done.TrySetResult(new Hit(download.Url, download.Page, download, null));
            };
            page.Close += (_, _) => { if (context.Pages.Count == 0) done.TrySetResult(null); };
        }

        // Some hosts send the file to a form post or a script, not a link: an attachment counts, whichever tab asked.
        context.Response += (_, response) =>
        {
            if (!IsFile(response, site)) return;
            if (site is not null && !site.IsFileHost(response.Url))
            {
                report.Say($"Ignored a file from {HostOf(response.Url)}: it isn't one of {site.Name}'s file hosts.");
                return;
            }
            IPage? page = null;
            try { page = response.Frame.Page; } catch (PlaywrightException) { /* from a worker; no page */ }
            response.Request.Headers.TryGetValue("referer", out var referer);
            done.TrySetResult(new Hit(response.Url, page, null, referer));
        };
        context.Page += (_, page) =>
        {
            Watch(page);
            _ = windows.KeepHiddenAsync(page); // a pop-up window opens wherever it likes
            if (site?.Popups != PopupHandling.Keep) _ = ClosePopupIfAdAsync(page, done.Task, report, windows);
        };
        browser.Disconnected += (_, _) => done.TrySetResult(null);
        using var cancelled = ct.Register(() => done.TrySetResult(null));

        var first = await context.NewPageAsync();
        Watch(first);
        await windows.KeepHiddenAsync(first);
        lock (_lock) _open = windows;
        if (!visible) BrowserWindows.GiveFocusBack(userWindow); // a hidden browser never keeps the focus it took starting
        // Picking the hidden browser on the taskbar (or with Alt+Tab) brings it on screen; nothing else does by itself.
        if (!visible && await BrowserProcessIdAsync(browser) is { } browserPid)
            _ = windows.ShowWhenPickedAsync(browserPid, done.Task);
        var userAgent = await first.EvaluateAsync<string>("() => navigator.userAgent");
        // Not awaited: a host page can keep loading forever, and the user may navigate away before it finishes.
        _ = first.GotoAsync(url).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var autopilot = site is null
            ? GenericAutopilot.RunAsync(context, done.Task, report, windows)
            : SiteStepRunner.RunAsync(site, context, done.Task, report, windows);

        var hit = await done.Task;
        try
        {
            ct.ThrowIfCancellationRequested(); // the browser still closes below
            if (hit is null) return null;
            var cookies = await context.CookiesAsync([hit.Url]);
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            var referer = !string.IsNullOrEmpty(hit.Referer) ? hit.Referer : hit.Page?.Url ?? url;
            if (hit.Download is { } download)
            {
                try { await download.CancelAsync(); } // TurboDL downloads it, not the browser
                catch (PlaywrightException) { /* already gone with its tab */ }
            }
            return new CapturedSession(hit.Url, userAgent, referer, cookieHeader);
        }
        finally
        {
            lock (_lock) _open = null;
            try { await browser.CloseAsync(); }
            catch (PlaywrightException) { /* the user closed it already */ }
            try { await autopilot; }
            catch (Exception) { /* it only drives the page; the result above stands either way */ }
        }
    }

    /// <summary>
    /// A response carrying a file: success, not a web page, and marked as an attachment. With a site config, a URL that
    /// matches its capture pattern counts without the mark, and so does a page navigation when the config doesn't
    /// require attachments (a host that opens the file in the tab instead of downloading it).
    /// </summary>
    private static bool IsFile(IResponse response, SiteConfig? site)
    {
        if (response.Status is not (200 or 206)) return false;
        // A page's own parts are never the file, whatever their headers say: Google's ad servers mark their scripts as
        // attachments ("f.txt"), and one was taken for a batch part.
        if (Subresources.Contains(response.Request.ResourceType)) return false;
        var headers = response.Headers; // lower-case names
        if (headers.TryGetValue("content-type", out var type)
            && (type.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                || type.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase))) return false;
        if (headers.TryGetValue("content-disposition", out var disposition)
            && disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase)) return true;
        if (site is null) return false;
        if (site.IsFileUrl(response.Url)) return true;
        return !site.RequireAttachment && response.Request.IsNavigationRequest;
    }

    /// <summary>Request kinds that load a part of a page (Playwright's resource types), never a download.</summary>
    private static readonly HashSet<string> Subresources = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "stylesheet", "image", "font", "media", "texttrack", "manifest", "ping", "eventsource", "websocket",
    };

    /// <summary>The browser's own process (every window of it belongs to that process); null when Chrome won't say.</summary>
    private static async Task<int?> BrowserProcessIdAsync(IBrowser browser)
    {
        try
        {
            var cdp = await browser.NewBrowserCDPSessionAsync();
            try
            {
                var info = await cdp.SendAsync("SystemInfo.getProcessInfo");
                if (info is { } json && json.TryGetProperty("processInfo", out var list))
                    foreach (var process in list.EnumerateArray())
                        if (process.TryGetProperty("type", out var type) && type.GetString() == "browser"
                            && process.TryGetProperty("id", out var id) && id.TryGetInt32(out var pid))
                            return pid;
            }
            finally
            {
                try { await cdp.DetachAsync(); } catch (PlaywrightException) { /* closing */ }
            }
        }
        catch (PlaywrightException)
        {
            // Not offered: the taskbar doesn't bring it on screen; Show browser and the autopilot still do.
        }
        return null;
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// A tab a page opened (not one the user opened) gets a few seconds to become the file or the host's own page;
    /// anything else is an ad: it's closed, and when the browser is on screen the page that opened it comes back to front.
    /// </summary>
    private static async Task ClosePopupIfAdAsync(IPage popup, Task finished, StatusReporter report, BrowserWindows windows)
    {
        try
        {
            if (await popup.OpenerAsync() is not { } opener) return;
            await Task.WhenAny(finished, Task.Delay(PopupGrace));
            if (finished.IsCompleted || popup.IsClosed) return;
            if (!popup.Url.StartsWith("about:", StringComparison.Ordinal) && Site(popup.Url) == Site(opener.Url)) return;
            await popup.CloseAsync();
            report.Say("Closed a pop-up the page opened.");
            if (windows.Shown && !opener.IsClosed) await opener.BringToFrontAsync(); // hidden: never take the focus
        }
        catch (PlaywrightException)
        {
            // Closed already, or the browser is going away.
        }
    }

    /// <summary>The last two labels of a URL's host ("dl.host.com" → "host.com"); enough to tell a host's pages from an ad's.</summary>
    private static string Site(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? string.Join('.', uri.Host.Split('.').TakeLast(2)) : "";

    /// <summary>Google Chrome when it's installed, otherwise Edge, which every Windows 10/11 PC has. Hidden ones open off-screen.</summary>
    private static async Task<IBrowser> LaunchAsync(IPlaywright playwright, bool visible)
    {
        string[] args = visible ? [] : [$"--window-position={BrowserWindows.OffScreen},{BrowserWindows.OffScreen}"];
        try
        {
            return await playwright.Chromium.LaunchAsync(new() { Headless = false, Channel = "chrome", Args = args });
        }
        catch (PlaywrightException)
        {
            return await playwright.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge", Args = args });
        }
    }
}
