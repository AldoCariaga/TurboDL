using System.Diagnostics;
using Microsoft.Playwright;
using TurboDL.Core.Sites;

namespace TurboDL.App.Services;

/// <summary>
/// Runs a site config's steps in order on the active page: the newest open tab on one of the config's hosts (so a pop-up
/// that is the host's own page takes over), else the first tab. Each step says what it does in the download's log. A
/// required step that doesn't finish in time ends the steps, and so does the last one; what happens then is the config's
/// <see cref="SiteConfig.WhenStepsEnd"/>: the general autopilot takes the page over, or the browser comes on screen.
/// The file is taken by <see cref="BrowserCapture"/> whenever it arrives, whatever step is running: the runner only
/// drives the page, and stops as soon as <c>finished</c> completes.
/// With <see cref="OffSiteHandling.GoBack"/>, an <see cref="OffSiteGuard"/> brings the host's tab back whenever it's sent
/// to another site (an ad script on the download button), and the steps start again from the first, a few times at most.
/// </summary>
internal static class SiteStepRunner
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    /// <summary>How many times the steps start again after the tab was brought back from another site.</summary>
    private const int MaxRestarts = 3;

    public static async Task RunAsync(SiteConfig site, IBrowserContext context, Task finished, StatusReporter report, BrowserWindows windows)
    {
        var prefix = $"Site config {site.Name}";
        var guard = site.OffSiteRedirects == OffSiteHandling.GoBack ? new OffSiteGuard(site, context, finished, report, windows) : null;
        var restarts = 0;
        for (var i = 0; i < site.Steps.Count; i++)
        {
            if (finished.IsCompleted) return;
            var step = site.Steps[i];
            report.Say($"{prefix} (step {i + 1} of {site.Steps.Count}): {step.Describe()}.");
            // A trip to another site cuts a step short (a wait, a countdown); the steps then start again below.
            var stop = guard is null ? finished : Task.WhenAny(finished, guard.Returned);
            bool done;
            try
            {
                done = await RunStepAsync(site, step, context, stop, report, windows);
            }
            catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException && finished.IsCompleted)
            {
                return; // the file came and the browser is closing
            }
            if (finished.IsCompleted) return;
            if (guard is not null && guard.Returned.IsCompleted)
            {
                guard.Rearm();
                if (restarts < MaxRestarts)
                {
                    restarts++;
                    report.Say($"{prefix}: starting the steps again ({restarts} of {MaxRestarts}).");
                    i = -1;
                    continue;
                }
                report.Say($"{prefix}: the page went to another site again; the steps aren't started again.");
                break;
            }
            if (done) continue;

            if (step.Optional)
            {
                report.Say($"{prefix}: step {i + 1} ({Name(step.Action)}) didn't happen in time; skipped, as it's optional.");
                continue;
            }
            report.Say($"{prefix}: step {i + 1} ({Name(step.Action)}) didn't finish in time.");
            break;
        }
        if (finished.IsCompleted) return;

        if (site.WhenStepsEnd == StepsEnd.Generic)
        {
            report.Say($"{prefix}: the general autopilot carries on from here.");
            await GenericAutopilot.RunAsync(context, finished, report, windows);
        }
        else if (Active(site, context) is { } page)
        {
            await windows.ShowAsync(page, $"{prefix}: the steps are done; the browser is on screen for you to finish.");
        }
    }

    /// <summary>True when the step finished; false when its time ran out.</summary>
    private static async Task<bool> RunStepAsync(SiteConfig site, SiteStep step, IBrowserContext context, Task finished,
        StatusReporter report, BrowserWindows windows)
    {
        var deadline = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(step.TimeoutSeconds);

        if (step.Action == SiteAction.Wait)
        {
            await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(step.Seconds)));
            return true;
        }

        // A page that navigates, or closes with a pop-up taking over, throws: look again on the active page until time's up.
        while (!finished.IsCompleted && deadline.Elapsed < limit)
        {
            if (Active(site, context) is not { } page)
            {
                await Task.WhenAny(finished, Task.Delay(Poll));
                continue;
            }
            var left = limit - deadline.Elapsed;
            try
            {
                switch (step.Action)
                {
                    case SiteAction.WaitFor:
                        await page.Locator(step.Selector!).First.WaitForAsync(new()
                        {
                            State = step.State switch
                            {
                                WaitState.Hidden => WaitForSelectorState.Hidden,
                                WaitState.Attached => WaitForSelectorState.Attached,
                                _ => WaitForSelectorState.Visible,
                            },
                            Timeout = Milliseconds(left),
                        });
                        return true;

                    case SiteAction.Click:
                        if (step.RetryIfIgnoredSeconds > 0) return await ClickUntilHeardAsync(site, step, page, finished, report, deadline, limit);
                        // A real mouse click; Playwright waits until the element is visible, enabled and still.
                        await page.Locator(step.Selector!).First.ClickAsync(new() { Timeout = Milliseconds(left) });
                        return true;

                    case SiteAction.Countdown:
                        if (await CountdownOverAsync(page, step.Selector!)) return true;
                        break;

                    case SiteAction.Captcha:
                        if (!await page.EvaluateAsync<bool>(CaptchaScript.Unsolved)) return true;
                        report.Say("Waiting for you to solve the CAPTCHA in the browser.");
                        await windows.ShowAsync(page, "The page asks for a CAPTCHA: the browser is on screen for you to solve it.");
                        break;
                }
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (PlaywrightException) when (!finished.IsCompleted)
            {
                // Navigating or closed: the next look picks the page up again.
            }
            await Task.WhenAny(finished, Task.Delay(Poll));
        }
        return finished.IsCompleted;
    }

    /// <summary>How many more clicks a click step with <see cref="SiteStep.RetryIfIgnoredSeconds"/> makes when the page sends nothing.</summary>
    private const int MaxClickRetries = 3;

    /// <summary>
    /// Clicks, then waits <see cref="SiteStep.RetryIfIgnoredSeconds"/> for the page to send something that a real click on
    /// the button sends: a navigation or a form post (not a GET for a page's part). Nothing means an ad script took the
    /// click (it opens a pop-up instead), so the button is clicked again, a few times at most; then the step counts as done
    /// and the steps go on as they would. Throws like a plain click (a timeout, a page that navigates or closes).
    /// </summary>
    private static async Task<bool> ClickUntilHeardAsync(SiteConfig site, SiteStep step, IPage page, Task finished,
        StatusReporter report, Stopwatch deadline, TimeSpan limit)
    {
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // An ad's frame loading, or a page's analytics beacon, isn't the button doing its work.
        void OnRequest(object? sender, IRequest request)
        {
            try
            {
                if (request.IsNavigationRequest ? request.Frame == page.MainFrame
                        : !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                          && request.ResourceType is not "ping"
                          && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && site.MatchLength(uri.IdnHost) >= 0)
                    heard.TrySetResult();
            }
            catch (PlaywrightException)
            {
                // From a worker (no frame): not the button.
            }
        }

        page.Request += OnRequest;
        try
        {
            for (var retries = 0; ; retries++)
            {
                var left = limit - deadline.Elapsed;
                await page.Locator(step.Selector!).First.ClickAsync(new() { Timeout = Milliseconds(left) });
                await Task.WhenAny(finished, heard.Task, Task.Delay(TimeSpan.FromSeconds(step.RetryIfIgnoredSeconds)));
                if (finished.IsCompleted || heard.Task.IsCompleted || page.IsClosed) return true;
                if (retries == MaxClickRetries || deadline.Elapsed >= limit)
                {
                    report.Say($"Site config {site.Name}: the page still sent nothing after the click; carrying on.");
                    return true;
                }
                report.Say($"Site config {site.Name}: the click didn't reach the page (most likely an ad took it); clicking again ({retries + 1} of {MaxClickRetries}).");
            }
        }
        finally
        {
            page.Request -= OnRequest;
        }
    }

    /// <summary>The countdown is over when its element is gone or hidden, or the number it shows is 0 (or it shows none).</summary>
    private static async Task<bool> CountdownOverAsync(IPage page, string selector)
    {
        var timer = page.Locator(selector).First;
        if (!await timer.IsVisibleAsync()) return true;
        string text;
        try
        {
            text = await timer.InnerTextAsync(new() { Timeout = 1000 });
        }
        catch (TimeoutException)
        {
            return false; // gone between the two looks: the next look sees it hidden
        }
        return SiteCountdown.SecondsLeft(text) is null or 0;
    }

    /// <summary>The newest open tab on one of the config's hosts, else the first open tab.</summary>
    private static IPage? Active(SiteConfig site, IBrowserContext context)
    {
        var pages = context.Pages.Where(p => !p.IsClosed).ToList();
        for (var i = pages.Count - 1; i >= 0; i--)
        {
            if (Uri.TryCreate(pages[i].Url, UriKind.Absolute, out var uri) && site.MatchLength(uri.IdnHost) >= 0) return pages[i];
        }
        return pages.FirstOrDefault();
    }

    private static float Milliseconds(TimeSpan left) => (float)Math.Max(1, left.TotalMilliseconds);

    /// <summary>
    /// Watches every tab that has been on one of the config's hosts: when one is sent to another site (the file never
    /// does that: a download doesn't replace the page), it goes back, or to the first host page it showed when going back
    /// doesn't land on the host. <see cref="Returned"/> completes each time; <see cref="Rearm"/> starts a new round. After
    /// <see cref="MaxReturns"/> trips the browser comes on screen for the user instead. It keeps watching after the steps
    /// end (the general autopilot can be sent to an ad too), until the file comes.
    /// </summary>
    private sealed class OffSiteGuard
    {
        private const int MaxReturns = 6;

        /// <summary>A moment for the file to arrive before an off-site page is taken for an ad.</summary>
        private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(700);

        private readonly SiteConfig _site;
        private readonly Task _finished;
        private readonly StatusReporter _report;
        private readonly BrowserWindows _windows;
        private readonly Lock _lock = new();
        private readonly Dictionary<IPage, string> _firstOnSite = [];
        private readonly HashSet<IPage> _returning = [];
        private TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _returns;

        public OffSiteGuard(SiteConfig site, IBrowserContext context, Task finished, StatusReporter report, BrowserWindows windows)
        {
            _site = site;
            _finished = finished;
            _report = report;
            _windows = windows;
            foreach (var page in context.Pages) Watch(page);
            context.Page += (_, page) => Watch(page);
        }

        public Task Returned
        {
            get { lock (_lock) return _returned.Task; }
        }

        public void Rearm()
        {
            lock (_lock)
                if (_returned.Task.IsCompleted) _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void Watch(IPage page) =>
            page.FrameNavigated += (_, frame) =>
            {
                if (frame == page.MainFrame) _ = OnNavigatedAsync(page, frame.Url);
            };

        private bool OnSite(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && _site.MatchLength(uri.IdnHost) >= 0;

        private async Task OnNavigatedAsync(IPage page, string url)
        {
            if (_finished.IsCompleted || url.StartsWith("about:", StringComparison.Ordinal)) return;
            string? home;
            lock (_lock)
            {
                if (OnSite(url))
                {
                    _firstOnSite.TryAdd(page, url);
                    return;
                }
                // A tab that was never on the host (an ad pop-up) is the pop-up rule's business; one already on its way back is left alone.
                if (!_firstOnSite.TryGetValue(page, out home) || !_returning.Add(page)) return;
            }
            try
            {
                await Task.WhenAny(_finished, Task.Delay(Grace));
                if (_finished.IsCompleted || page.IsClosed || OnSite(page.Url)) return;

                var trip = Interlocked.Increment(ref _returns);
                var where = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ? uri.Host : page.Url;
                if (trip > MaxReturns)
                {
                    if (trip == MaxReturns + 1)
                        await _windows.ShowAsync(page, $"Site config {_site.Name}: the page keeps going to other sites; the browser is on screen for you to finish it.");
                    return;
                }
                _report.Say($"Site config {_site.Name}: the page went to another site ({where}), most likely an ad; going back.");
                try
                {
                    await page.GoBackAsync(new() { Timeout = 15_000 });
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                {
                    // No history, or it didn't load: the host page is opened again below.
                }
                if (!_finished.IsCompleted && !page.IsClosed && !OnSite(page.Url))
                {
                    try { await page.GotoAsync(home, new() { Timeout = 30_000 }); }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { /* the steps time out and say so */ }
                }
                lock (_lock) _returned.TrySetResult();
            }
            finally
            {
                lock (_lock) _returning.Remove(page);
            }
        }
    }

    private static string Name(SiteAction action) => action switch
    {
        SiteAction.WaitFor => "waitFor",
        SiteAction.Countdown => "countdown",
        SiteAction.Captcha => "captcha",
        SiteAction.Click => "click",
        _ => "wait",
    };
}
