using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace TurboDL.App.Services;

/// <summary>
/// The general autopilot for a host with no site config (and for one whose config's steps ran out): every second, on
/// every tab, it reads the page with <see cref="HostPageScript"/>, waits while a countdown runs or a CAPTCHA is unsolved,
/// and otherwise clicks the download button it finds by its wording, with a real mouse click.
/// </summary>
internal static class GenericAutopilot
{
    /// <summary>A countdown showing the same number this long isn't counting: it's page text, and is ignored.</summary>
    private static readonly TimeSpan StaleTimer = TimeSpan.FromSeconds(10);

    /// <summary>A "Generating download link..." shown this long is page text too (making a link takes seconds, not this).</summary>
    private static readonly TimeSpan StaleBusy = TimeSpan.FromSeconds(30);

    /// <summary>A hidden browser whose pages show no change this long needs the user: it's brought on screen.</summary>
    private static readonly TimeSpan Stuck = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>How a waiting state with no number is kept in the per-page timer table.</summary>
    private const int Busy = -1;

    /// <summary>
    /// Runs until <paramref name="finished"/> completes. A CAPTCHA, or pages that show no change for <see cref="Stuck"/>,
    /// bring a hidden browser on screen. Pages that are loading or closing are skipped until the next round.
    /// </summary>
    public static async Task RunAsync(IBrowserContext context, Task finished, StatusReporter report, BrowserWindows windows)
    {
        var timers = new Dictionary<IPage, (int Seconds, DateTime Since)>();
        var lastChange = DateTime.UtcNow;
        var lastLook = "";
        while (!finished.IsCompleted)
        {
            var look = new List<string>();
            foreach (var page in context.Pages.ToList())
            {
                if (finished.IsCompleted) return;
                try
                {
                    var stale = timers.TryGetValue(page, out var timer)
                                && DateTime.UtcNow - timer.Since > (timer.Seconds == Busy ? StaleBusy : StaleTimer);
                    var answer = await page.EvaluateAsync<string>(HostPageScript.Scan,
                        new { ignoreTimers = stale && timer.Seconds != Busy, ignoreBusy = stale && timer.Seconds == Busy });
                    look.Add(page.Url + " " + answer);
                    var (kind, detail) = answer.IndexOf('|') is var bar and >= 0 ? (answer[..bar], answer[(bar + 1)..]) : (answer, "");
                    switch (kind)
                    {
                        case "captcha":
                            report.Say("Waiting for you to solve the CAPTCHA in the browser.");
                            await windows.ShowAsync(page, "The page asks for a CAPTCHA: the browser is on screen for you to solve it.");
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

            // Anything different (a countdown ticking, a click, another page) is progress; nothing for a while needs the user.
            var now = string.Join("\n", look);
            if (now != lastLook)
            {
                lastLook = now;
                lastChange = DateTime.UtcNow;
            }
            else if (!windows.Shown && DateTime.UtcNow - lastChange > Stuck && context.Pages.FirstOrDefault() is { } stuck)
            {
                await windows.ShowAsync(stuck, "The page isn't moving on by itself: the browser is on screen for you to finish it.");
            }
            await Task.WhenAny(finished, Task.Delay(ScanInterval));
        }
    }
}

/// <summary>
/// The browser's windows: kept off-screen while hidden, brought on screen (once) when the user is needed or picks the
/// browser on the taskbar. It's a normal window either way, only placed out of sight, so pages behave as they would on
/// screen.
/// </summary>
internal sealed partial class BrowserWindows(IBrowserContext context, StatusReporter report, bool shown)
{
    /// <summary>Where a hidden window sits: far outside every screen, so it never shows or takes the focus.</summary>
    public const int OffScreen = -32000;

    /// <summary>How often <see cref="ShowWhenPickedAsync"/> looks at which window is in front.</summary>
    private static readonly TimeSpan PickPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>A window this new in front was opened by the page (a pop-up), not picked by the user.</summary>
    private static readonly TimeSpan NewWindowGrace = TimeSpan.FromSeconds(2);

    private int _shown = shown ? 1 : 0;
    private long _lastNewPageTicks = Environment.TickCount64;

    public bool Shown => Volatile.Read(ref _shown) == 1;

    /// <summary>
    /// Brings the hidden browser on screen when the user brings it to the front themselves: its taskbar button, Alt+Tab.
    /// Only a change counts (another window was in front, now one of the browser's is), so the browser being in front
    /// when it starts, or a pop-up the page just opened, doesn't. Ends when the browser is shown or the capture ends.
    /// </summary>
    public async Task ShowWhenPickedAsync(int browserProcessId, Task finished)
    {
        try
        {
            var wasInFront = InFront(browserProcessId);
            while (!finished.IsCompleted && !Shown)
            {
                await Task.WhenAny(finished, Task.Delay(PickPoll));
                var inFront = InFront(browserProcessId);
                var popup = Environment.TickCount64 - Volatile.Read(ref _lastNewPageTicks) < NewWindowGrace.TotalMilliseconds;
                if (inFront && !wasInFront && !popup && !finished.IsCompleted
                    && context.Pages.LastOrDefault(p => !p.IsClosed) is { } page)
                {
                    await ShowAllAsync("You picked the browser on the taskbar: it's on screen now.");
                    if (!page.IsClosed) await page.BringToFrontAsync();
                    return;
                }
                wasInFront = inFront;
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
            // The browser is closing.
        }
    }

    private static bool InFront(int processId)
    {
        var window = GetForegroundWindow();
        return window != 0 && GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId;
    }

    /// <summary>The last window in front that wasn't a hidden browser: where the focus goes back to.</summary>
    private static nint _userWindow;

    /// <summary>
    /// The window the user is in, taken before a hidden browser starts: the one in front, or, when that's another
    /// hidden browser (several start at once), the one in front before it.
    /// </summary>
    public static nint UserWindow()
    {
        var window = GetForegroundWindow();
        if (window == 0 || IsOffScreen(window)) return Volatile.Read(ref _userWindow);
        Volatile.Write(ref _userWindow, window);
        return window;
    }

    /// <summary>
    /// A starting Chrome takes the focus for its own (hidden) window, so typing would go nowhere: when a hidden window
    /// is in front, the focus goes back to <paramref name="userWindow"/>. Windows only lets the window in front hand
    /// the focus on, so this joins its input queue for the moment it takes.
    /// </summary>
    public static void GiveFocusBack(nint userWindow)
    {
        var front = GetForegroundWindow();
        if (userWindow == 0 || front == 0 || front == userWindow || !IsOffScreen(front) || !IsWindow(userWindow)) return;
        var frontThread = GetWindowThreadProcessId(front, out _);
        var thisThread = GetCurrentThreadId();
        var joined = frontThread != thisThread && AttachThreadInput(thisThread, frontThread, true);
        try
        {
            SetForegroundWindow(userWindow);
        }
        finally
        {
            if (joined) AttachThreadInput(thisThread, frontThread, false);
        }
    }

    /// <summary>A window placed far outside every screen: a hidden browser (minimized windows sit there too).</summary>
    private static bool IsOffScreen(nint window) => GetWindowRect(window, out var rect) && rect.Right <= OffScreen + 10_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool join);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    /// <summary>Moves the page's window off-screen while the browser is hidden.</summary>
    public async Task KeepHiddenAsync(IPage page)
    {
        Volatile.Write(ref _lastNewPageTicks, Environment.TickCount64);
        if (Shown) return;
        try
        {
            await SetBoundsAsync(page, new() { ["left"] = OffScreen, ["top"] = OffScreen });
        }
        catch (PlaywrightException)
        {
            // Closed already (an ad that shut itself), or the browser is going away.
        }
    }

    /// <summary>Brings the browser on screen with <paramref name="page"/> in front, and says why. Once only.</summary>
    public async Task ShowAsync(IPage page, string why)
    {
        if (Interlocked.Exchange(ref _shown, 1) == 1) return;
        report.Say(why);
        await RevealAsync(page);
        await page.BringToFrontAsync();
    }

    /// <summary>Brings every window on screen (the user asked), the first tab in front.</summary>
    public async Task ShowAllAsync(string why)
    {
        if (Interlocked.Exchange(ref _shown, 1) == 1) return;
        report.Say(why);
        var pages = context.Pages.ToList();
        foreach (var page in pages)
        {
            try { await RevealAsync(page); }
            catch (PlaywrightException) { /* closed meanwhile */ }
        }
        if (pages.FirstOrDefault(p => !p.IsClosed) is { } front) await front.BringToFrontAsync();
    }

    private Task RevealAsync(IPage page) =>
        SetBoundsAsync(page, new() { ["left"] = 80, ["top"] = 60, ["width"] = 1280, ["height"] = 880 });

    /// <summary>Sets the bounds of the window <paramref name="page"/> is in (Chrome's own window commands).</summary>
    private async Task SetBoundsAsync(IPage page, Dictionary<string, object> bounds)
    {
        var session = await context.NewCDPSessionAsync(page);
        try
        {
            var window = await session.SendAsync("Browser.getWindowForTarget");
            var id = window!.Value.GetProperty("windowId").GetInt32();
            // A minimized or maximized window has to be normal before it can be moved.
            await session.SendAsync("Browser.setWindowBounds", new()
            {
                ["windowId"] = id,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
            });
            await session.SendAsync("Browser.setWindowBounds", new() { ["windowId"] = id, ["bounds"] = bounds });
        }
        finally
        {
            try { await session.DetachAsync(); } catch (PlaywrightException) { /* gone with its page */ }
        }
    }
}

/// <summary>Passes on status lines, skipping a line that repeats the one before it (a countdown is reported once).</summary>
internal sealed class StatusReporter(IProgress<string>? status)
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
