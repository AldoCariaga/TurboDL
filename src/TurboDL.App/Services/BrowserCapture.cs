using Microsoft.Playwright;
using TurboDL.Core.Browser;
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
/// Opens a visible Chrome (or Edge) window on a file host's page and clicks through it: it waits out countdowns, clicks the
/// download button (found by its text, see <see cref="HostPageScript"/>), closes ad pop-ups, and leaves CAPTCHAs and
/// sign-ins to the user, who can also click anything themselves. As soon as the host sends the file (a browser download,
/// or any response marked as an attachment), the link, cookies, user agent and referring page are taken and the browser
/// is closed; TurboDL then downloads the file itself. Playwright's driver comes from <see cref="PlaywrightTools"/>.
/// </summary>
public sealed class BrowserCapture(PlaywrightTools tools)
{
    /// <summary>How long a pop-up gets to turn out to be the file, or the host's own page, before it's closed as an ad.</summary>
    private static readonly TimeSpan PopupGrace = TimeSpan.FromSeconds(3);

    /// <summary>A countdown showing the same number this long isn't counting: it's page text, and is ignored.</summary>
    private static readonly TimeSpan StaleTimer = TimeSpan.FromSeconds(10);

    /// <summary>A "Generating download link..." shown this long is page text too (making a link takes seconds, not this).</summary>
    private static readonly TimeSpan StaleBusy = TimeSpan.FromSeconds(30);

    /// <summary>How a waiting state with no number is kept in the per-page timer table.</summary>
    private const int Busy = -1;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>True until the browser helper has been downloaded.</summary>
    public bool NeedsSetup => !tools.IsInstalled;

    /// <summary>Where the file was found, and the page it came from.</summary>
    private sealed record Hit(string Url, IPage? Page, IDownload? Download, string? Referer);

    /// <summary>
    /// Waits until the host sends a file from any tab. Null when the user closes the browser first.
    /// <paramref name="status"/> hears what the page is doing (each change once), for the download's log.
    /// Cancelling <paramref name="ct"/> (Pause) closes the browser and throws OperationCanceledException.
    /// </summary>
    public async Task<CapturedSession?> CaptureAsync(string url, IProgress<ToolSetupProgress>? setup = null,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        await tools.EnsureAsync(setup, ct);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", tools.Directory);

        using var playwright = await Playwright.CreateAsync();
        ct.ThrowIfCancellationRequested();
        await using var browser = await LaunchAsync(playwright);
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        var done = new TaskCompletionSource<Hit?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var report = new StatusReporter(status);

        void Watch(IPage page)
        {
            page.Download += (_, download) => done.TrySetResult(new Hit(download.Url, download.Page, download, null));
            page.Close += (_, _) => { if (context.Pages.Count == 0) done.TrySetResult(null); };
        }

        // Some hosts send the file to a form post or a script, not a link: any attachment counts, whichever tab asked.
        context.Response += (_, response) =>
        {
            if (!IsFile(response)) return;
            IPage? page = null;
            try { page = response.Frame.Page; } catch (PlaywrightException) { /* from a worker; no page */ }
            response.Request.Headers.TryGetValue("referer", out var referer);
            done.TrySetResult(new Hit(response.Url, page, null, referer));
        };
        context.Page += (_, page) =>
        {
            Watch(page);
            _ = ClosePopupIfAdAsync(page, done.Task, report);
        };
        browser.Disconnected += (_, _) => done.TrySetResult(null);
        using var cancelled = ct.Register(() => done.TrySetResult(null));

        var first = await context.NewPageAsync();
        Watch(first);
        var userAgent = await first.EvaluateAsync<string>("() => navigator.userAgent");
        // Not awaited: a host page can keep loading forever, and the user may navigate away before it finishes.
        _ = first.GotoAsync(url).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var autopilot = ClickThroughAsync(context, done.Task, report);

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
            try { await browser.CloseAsync(); }
            catch (PlaywrightException) { /* the user closed it already */ }
            try { await autopilot; }
            catch (Exception) { /* it only drives the page; the result above stands either way */ }
        }
    }

    /// <summary>A response carrying a file: success, marked as an attachment, and not a web page.</summary>
    private static bool IsFile(IResponse response)
    {
        if (response.Status is not (200 or 206)) return false;
        var headers = response.Headers; // lower-case names
        if (!headers.TryGetValue("content-disposition", out var disposition)
            || !disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase)) return false;
        return !(headers.TryGetValue("content-type", out var type) && type.Contains("text/html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every second, on every tab: waits while a countdown runs or a CAPTCHA is unsolved, otherwise clicks the download
    /// button, with a real mouse click. Pages that are loading or closing are skipped until the next round.
    /// </summary>
    private static async Task ClickThroughAsync(IBrowserContext context, Task finished, StatusReporter report)
    {
        var timers = new Dictionary<IPage, (int Seconds, DateTime Since)>();
        while (!finished.IsCompleted)
        {
            foreach (var page in context.Pages.ToList())
            {
                if (finished.IsCompleted) return;
                try
                {
                    var stale = timers.TryGetValue(page, out var timer)
                                && DateTime.UtcNow - timer.Since > (timer.Seconds == Busy ? StaleBusy : StaleTimer);
                    var answer = await page.EvaluateAsync<string>(HostPageScript.Scan,
                        new { ignoreTimers = stale && timer.Seconds != Busy, ignoreBusy = stale && timer.Seconds == Busy });
                    var (kind, detail) = answer.IndexOf('|') is var bar and >= 0 ? (answer[..bar], answer[(bar + 1)..]) : (answer, "");
                    switch (kind)
                    {
                        case "captcha":
                            report.Say("Waiting for you to solve the CAPTCHA in the browser.");
                            break;
                        case "timer":
                            var seconds = int.Parse(detail);
                            if (!timers.TryGetValue(page, out timer) || timer.Seconds != seconds) timers[page] = (seconds, DateTime.UtcNow);
                            report.Say("Waiting for the host's countdown.");
                            break;
                        case "busy":
                            if (!timers.TryGetValue(page, out timer) || timer.Seconds != Busy) timers[page] = (Busy, DateTime.UtcNow);
                            report.Say("Waiting for the host to make the download link.");
                            break;
                        case "click":
                            timers.Remove(page);
                            report.Say($"Clicking \"{detail}\".");
                            await page.Locator(HostPageScript.TargetSelector).First.ClickAsync(new() { Timeout = 5000 });
                            break;
                        default:
                            if (!stale) timers.Remove(page); // a stuck "countdown" stays ignored
                            break;
                    }
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or ObjectDisposedException)
                {
                    // Navigating, closed, or the click was blocked (an overlay): try again next round.
                }
            }
            await Task.WhenAny(finished, Task.Delay(ScanInterval));
        }
    }

    /// <summary>
    /// A tab a page opened (not one the user opened) gets a few seconds to become the file or the host's own page;
    /// anything else is an ad: it's closed and the page that opened it comes back to the front.
    /// </summary>
    private static async Task ClosePopupIfAdAsync(IPage popup, Task finished, StatusReporter report)
    {
        try
        {
            if (await popup.OpenerAsync() is not { } opener) return;
            await Task.WhenAny(finished, Task.Delay(PopupGrace));
            if (finished.IsCompleted || popup.IsClosed) return;
            if (!popup.Url.StartsWith("about:", StringComparison.Ordinal) && Site(popup.Url) == Site(opener.Url)) return;
            await popup.CloseAsync();
            report.Say("Closed a pop-up the page opened.");
            if (!opener.IsClosed) await opener.BringToFrontAsync();
        }
        catch (PlaywrightException)
        {
            // Closed already, or the browser is going away.
        }
    }

    /// <summary>The last two labels of a URL's host ("dl.host.com" → "host.com"); enough to tell a host's pages from an ad's.</summary>
    private static string Site(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? string.Join('.', uri.Host.Split('.').TakeLast(2)) : "";

    /// <summary>Google Chrome when it's installed, otherwise Edge, which every Windows 10/11 PC has.</summary>
    private static async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync(new() { Headless = false, Channel = "chrome" });
        }
        catch (PlaywrightException)
        {
            return await playwright.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge" });
        }
    }

    /// <summary>Passes on status lines, skipping a line that repeats the one before it (a countdown is reported once).</summary>
    private sealed class StatusReporter(IProgress<string>? status)
    {
        private readonly Lock _lock = new();
        private string? _last;

        public void Say(string line)
        {
            lock (_lock)
            {
                if (line == _last) return;
                _last = line;
            }
            status?.Report(line);
        }
    }
}
