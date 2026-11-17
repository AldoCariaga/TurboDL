using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TurboDL.Core.Archives;
using TurboDL.Core.Browser;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;
using TurboDL.Core.Sites;
using TurboDL.Core.Storage;
using TurboDL.Core.Video;
using TurboDL.App.ViewModels;

namespace TurboDL.App.Services;

/// <summary>Asks the user things the engine can't decide alone.</summary>
public interface IUserPrompts
{
    /// <summary>The server's copy changed since the download began. True = throw away the partial file and start over.</summary>
    Task<bool> ConfirmRestartAsync(string fileName);
}

/// <summary>
/// Owns every download: the list, the running tasks, the shared <see cref="HttpClient"/> and the store.
/// All public members are called on the UI thread.
/// </summary>
public sealed class DownloadManager
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly HttpClient http;
    private readonly RateLimiter globalLimiter;

    /// <summary>One for every download, so many links to one host don't flood it (a batch of 79 parts starting at once).</summary>
    private readonly HostThrottle hostThrottle = new();
    private readonly DownloadStore store;
    private readonly IUserPrompts prompts;
    private readonly VideoToolsService videoTools;
    private readonly BrowserCapture browser;
    private readonly SiteConfigService sites;
    private readonly ILogger<DownloadManager> log;

    /// <summary>
    /// How many host pages are solved at once (Settings: Pages solved at once), each in its own browser; a batch whose
    /// parts all hit a host page goes through them that many at a time, in order.
    /// </summary>
    private readonly SolvingGate _browserGate;

    /// <summary>Numbers each wait for the browser, in order.</summary>
    private long _browserTickets;

    /// <summary>
    /// Batch id → waits numbered below this are skipped: the user closed the browser without a download while they
    /// were waiting, which means "stop", not "next part". Later ones (Retry failed parts) open again.
    /// </summary>
    private readonly Dictionary<long, long> _browserSkip = [];

    public DownloadManager(HttpClient http, RateLimiter globalLimiter, DownloadStore store, IUserPrompts prompts,
        VideoToolsService videoTools, BrowserCapture browser, SiteConfigService sites, SettingsService settings,
        ILogger<DownloadManager> log)
    {
        _browserGate = new SolvingGate(settings.Current.MaxSolving);
        settings.Changed += s => _browserGate.Limit = s.MaxSolving;
        this.sites = sites;
        this.http = http;
        this.globalLimiter = globalLimiter;
        this.store = store;
        this.prompts = prompts;
        this.videoTools = videoTools;
        this.browser = browser;
        this.log = log;
        // yt-dlp takes its cap on the command line, so a new global limit means restarting running videos.
        settings.Changed += _ =>
        {
            foreach (var item in Items.Where(i => i.VideoJob is { } job
                         && job.RateLimit != VideoJob.EffectiveLimit(i.Info.SpeedLimit ?? 0, globalLimiter.BytesPerSecond)).ToList())
                Restart(item);
        };
    }

    /// <summary>True while any video download runs (the tools must not be updated then).</summary>
    public bool AnyVideoRunning => Items.Any(i => i.VideoJob is not null);

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    public HttpClient Http => http;

    /// <summary>Raised on the UI thread when a download finishes. Hook for the tray toast.</summary>
    public event Action<DownloadItemViewModel>? Completed;

    /// <summary>Raised on the UI thread whenever a run ends: finished, paused or failed. The queue listens to this.</summary>
    public event Action<DownloadItemViewModel>? Ended;

    /// <summary>Raised on the UI thread when a download switches to a link the browser found, just before it starts again.</summary>
    public event Action<DownloadItemViewModel>? SourceReplaced;

    /// <summary>
    /// Raised on the UI thread when a download leaves its browser step: downloading the link it found, or not (closed,
    /// paused, failed). Either way its slot may now be free; the queue and batches listen.
    /// </summary>
    public event Action<DownloadItemViewModel>? BrowserDone;

    /// <summary>Raised on the UI thread when the user points a stopped download at a new link; it's Queued again.</summary>
    public event Action<DownloadItemViewModel>? LinkChanged;

    /// <summary>
    /// Loads the saved list. Anything still marked Downloading was running when the app last closed (or crashed),
    /// so it starts again.
    /// </summary>
    public void Load()
    {
        var resume = new List<DownloadItemViewModel>();
        foreach (var info in store.LoadAll())
        {
            var item = new DownloadItemViewModel(info);
            Items.Add(item);
            if (info.Status == DownloadStatus.Downloading) resume.Add(item);
        }
        foreach (var item in resume) Start(item);
    }

    public DownloadItemViewModel Add(DownloadInfo info, bool startNow)
    {
        info.Status = DownloadStatus.Queued;
        store.Add(info);
        var item = new DownloadItemViewModel(info);
        if (startNow) Start(item); // before it joins the list, so the queue never mistakes it for a waiting one
        Items.Add(item);
        return item;
    }

    /// <summary>Starts or resumes. Resuming is a new task over the same info; the engine picks up from the saved segments.</summary>
    public void Start(DownloadItemViewModel item)
    {
        if (item.IsRunning || item.InBrowser || item.Info.Status == DownloadStatus.Completed) return;
        // A handed-over link that failed for good (expired, refused, or not the file) isn't tried again: its page is.
        if (item.Info is { Status: DownloadStatus.Failed, ErrorIsTransient: false, PageUrl: not null } && ReturnToPage(item.Info))
        {
            store.Save(item.Info);
            item.AddLog("Starting again from the host's page, since the link the browser found didn't work.");
        }
        // A host whose site config says its links only work in a browser: straight to the browser step, which holds the
        // download's slot the same way a host page found over HTTP does.
        if (!item.IsVideo && BrowserFirstSite(item.Info) is { } site && EnterBrowser(item))
        {
            item.AddLog($"Site config {site.Name}: this host's links go straight to the browser.");
            _ = RunBrowserAsync(item, userAsked: false, browserFirst: true);
            return;
        }
        item.Run = item.IsVideo ? RunVideoAsync(item) : RunAsync(item);
    }

    /// <summary>The site config of the download's host, when it sends links straight to the browser and the link is still the host's page.</summary>
    private SiteConfig? BrowserFirstSite(DownloadInfo info) =>
        info.PageUrl is null && SiteFor(info.Url) is { BrowserFirst: true } site ? site : null;

    private SiteConfig? SiteFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? sites.Catalog.Match(uri) : null;

    /// <summary>
    /// Stops a download. A restart that was on its way (new speed limit) is called off: pausing wins. One waiting for the
    /// browser, or in it, stops too (the browser closes) and becomes Paused.
    /// </summary>
    public void Pause(DownloadItemViewModel item)
    {
        item.RestartRequested = false;
        item.Cts?.Cancel();
        item.BrowserCts?.Cancel();
    }

    /// <summary>Puts a stopped download back in the queue so it starts again when the queue does.</summary>
    public void Requeue(DownloadItemViewModel item)
    {
        if (item.IsRunning || item.Info.Status == DownloadStatus.Completed) return;
        item.Info.Status = DownloadStatus.Queued;
        store.Save(item.Info);
        item.Refresh();
    }

    /// <summary>
    /// Marks a finished download as failed after a check found it bad (e.g. a web page saved as a .rar). The bad file is
    /// deleted and the progress reset, so starting it again downloads from scratch. Not retried automatically.
    /// </summary>
    public void MarkFailed(DownloadItemViewModel item, string message)
    {
        var info = item.Info;
        if (File.Exists(info.FilePath)) TryDelete(info.FilePath, recycle: false);
        // A link the browser handed over gave the wrong thing: the retry starts from the host's page again.
        ReturnToPage(info);
        // FileName stays, so the retry saves under the same name.
        info.Segments = [];
        info.TotalSize = null;
        info.ETag = null;
        info.LastModified = null;
        info.CompletedUtc = null;
        info.Status = DownloadStatus.Failed;
        info.Error = message;
        info.ErrorIsTransient = false;
        store.Save(info);
        item.AddLog(message);
        item.Refresh();
    }

    /// <summary>
    /// Opens the download's link in a visible browser for the user to click through. When the page starts the file, the
    /// download switches to that direct link with the browser's session and starts again. Up to Pages solved at once
    /// browsers run together (<see cref="SolvingGate"/>); the rest wait their turn, in order. Called from the row's menu
    /// (<paramref name="userAsked"/>), and on its own when a host answers with a web page or refuses a link that must give a file. Closing the browser without a download skips the other parts
    /// of that batch already waiting for it. While waiting and in the browser the download holds its "downloads at once"
    /// slot, and Pause stops it.
    /// </summary>
    public Task OpenInBrowserAsync(DownloadItemViewModel item, bool userAsked = false) =>
        EnterBrowser(item) ? RunBrowserAsync(item, userAsked) : Task.CompletedTask;

    /// <summary>Show browser: brings the hidden browser solving this download's page on screen.</summary>
    public async Task ShowBrowserAsync(DownloadItemViewModel item)
    {
        if (item.Browser != BrowserStage.Open || item.BrowserView is not { } view)
        {
            if (item.InBrowser)
                item.AddLog($"Not in the browser yet: {_browserGate.Limit} pages are solved at once, and this one waits its turn.");
            return;
        }
        try
        {
            await view.ShowAsync();
        }
        catch (Exception ex)
        {
            item.AddLog("Browser: " + ex.Message);
        }
    }

    /// <summary>Puts the download in the wait for the browser. False when it can't go (a video, running, already there).</summary>
    private bool EnterBrowser(DownloadItemViewModel item)
    {
        if (item.IsVideo || item.IsRunning || item.InBrowser) return false;
        item.BrowserTicket = ++_browserTickets;
        item.BrowserCts = new CancellationTokenSource();
        item.SetBrowser(BrowserStage.Waiting);
        return true;
    }

    /// <summary>
    /// <paramref name="browserFirst"/>: it never tried the link over HTTP (its site config said so), so a browser closed
    /// without the file leaves it Failed; otherwise it would stay Queued and the queue would open the browser again.
    /// </summary>
    private async Task RunBrowserAsync(DownloadItemViewModel item, bool userAsked, bool browserFirst = false)
    {
        var ct = item.BrowserCts!.Token;
        CapturedSession? captured = null;
        string? failure = null;
        try
        {
            await _browserGate.WaitAsync(ct);
            try
            {
                if (!Items.Contains(item)) return; // removed while waiting
                if (!userAsked && item.Info.BatchId is { } waitingBatch
                    && _browserSkip.TryGetValue(waitingBatch, out var skipBelow) && item.BrowserTicket < skipBelow)
                {
                    failure = "Not opened in the browser: it was closed without a download for another part of this batch. " +
                              "Right-click and choose Open in browser, or Retry failed parts, to try again.";
                    item.AddLog(failure);
                    return;
                }
                if (browser.NeedsSetup)
                    item.AddLog($"Setting up the browser helper ({PlaywrightTools.SetupSizeText}, first time only).");
                item.SetBrowser(BrowserStage.Open);
                item.AddLog(userAsked
                    ? "Opening the link in a browser. TurboDL clicks through the page; solve any CAPTCHA or sign-in yourself."
                    : "Solving the host's page in a hidden browser. It comes on screen if a CAPTCHA or sign-in needs you.");
                // Shown from the start only when the user asked for the browser; otherwise hidden until it needs them.
                // The host's page, not a direct link an earlier browser found (that one has expired, or wasn't the file).
                var pageUrl = item.Info.PageUrl ?? item.Info.Url;
                item.BrowserView = new BrowserView();
                var session = await browser.CaptureAsync(pageUrl, status: new Progress<string>(item.AddLog), ct: ct,
                    visible: userAsked, site: SiteFor(pageUrl), view: item.BrowserView);
                if (session is null)
                {
                    failure = "The browser was closed before a download started.";
                    item.AddLog(failure);
                    if (item.Info.BatchId is { } closedBatch)
                    {
                        _browserSkip[closedBatch] = _browserTickets + 1; // every part waiting right now
                        var skipped = Items.Count(i => i != item && i.InBrowser && i.Info.BatchId == closedBatch);
                        if (skipped > 0)
                            item.AddLog($"The other {skipped} parts of this batch waiting for the browser are skipped. " +
                                        "Use Retry failed parts to go through them again.");
                    }
                    return;
                }
                if (!Items.Contains(item)) return;
                item.AddLog($"Browser handed over {session.Url}");
                captured = session;
            }
            finally
            {
                _browserGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Paused (or TurboDL is closing): Resume runs it again, and a host page brings the browser back.
            if (Items.Contains(item))
            {
                item.Info.Status = DownloadStatus.Paused;
                store.Save(item.Info);
                item.AddLog("Paused. Resume to open the browser again.");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Browser for download {Id} failed", item.Info.Id);
            failure = "Browser: " + ex.Message;
            item.AddLog(failure);
        }
        finally
        {
            item.BrowserCts?.Dispose();
            item.BrowserCts = null;
            item.BrowserView = null;
            item.SetBrowser(BrowserStage.None);
        }

        // Out of the browser step and straight into downloading, so the download keeps the slot the browser step held.
        if (captured is not null && Items.Contains(item)) UseCapturedSource(item, captured.Url, captured.Headers());
        else if (browserFirst && failure is not null && Items.Contains(item))
        {
            item.Info.Status = DownloadStatus.Failed;
            item.Info.Error = failure;
            item.Info.ErrorIsTransient = false;
            store.Save(item.Info);
            item.Refresh();
        }
        BrowserDone?.Invoke(item);
    }

    /// <summary>
    /// Points a stopped download at the direct link a browser found, with that browser's session headers, and starts it
    /// from scratch, in the slot its browser step held. A file already saved (a host's web page) is deleted. A batch
    /// part keeps the name it was added with; otherwise the server names the file.
    /// </summary>
    private void UseCapturedSource(DownloadItemViewModel item, string url, IReadOnlyDictionary<string, string> headers)
    {
        if (item.IsBusy) return;
        var info = item.Info;
        TryDelete(info.TempPath, recycle: false);
        if (info.Status == DownloadStatus.Completed && !string.IsNullOrEmpty(info.FileName))
            TryDelete(info.FilePath, recycle: false);

        if (info.PageUrl is null)
        {
            info.PageUrl = info.Url;
            info.PageHeaders = new(info.Headers);
        }
        info.Url = url;
        info.FinalUrl = null;
        info.FileName = info.RequestedFileName ?? "";
        info.Segments = [];
        info.TotalSize = null;
        info.ETag = null;
        info.LastModified = null;
        info.CompletedUtc = null;
        info.Error = null;
        info.ErrorIsTransient = false;
        info.Status = DownloadStatus.Queued;
        foreach (var (name, value) in headers)
            if (!string.IsNullOrWhiteSpace(value)) info.Headers[name] = value;
        store.Save(info);
        item.Refresh();
        SourceReplaced?.Invoke(item);
        Start(item);
    }

    /// <summary>
    /// Points a download whose link a browser handed over back at the host's page it came from, with the headers it had
    /// then, and forgets the progress on the old link. False when there's no page to go back to.
    /// </summary>
    private static bool ReturnToPage(DownloadInfo info)
    {
        if (info.PageUrl is null) return false;
        info.Url = info.PageUrl;
        if (info.PageHeaders is { } before)
        {
            info.Headers = new(before);
        }
        else
        {
            // Handed over before the old headers were kept: drop the browser session's.
            foreach (var key in info.Headers.Keys.Where(k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                         || k.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                         || k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).ToList())
                info.Headers.Remove(key);
        }
        info.PageUrl = null;
        info.PageHeaders = null;
        info.FinalUrl = null;
        info.Segments = [];
        info.TotalSize = null;
        info.ETag = null;
        info.LastModified = null;
        return true;
    }

    /// <summary>A stopped plain download (queued, paused or failed) can be pointed at another link. Not a video, nor one that's running or in the browser.</summary>
    public static bool CanChangeLink(DownloadItemViewModel item) => item.CanChangeLink;

    /// <summary>
    /// Points a stopped download at a new link (an expired link, a mirror) under <paramref name="fileName"/>, and puts it
    /// back in the queue. What was downloaded from the old link is thrown away. A link a browser handed over is forgotten
    /// together with that browser's session headers; the ones the download was added with stay.
    /// </summary>
    public void ChangeLink(DownloadItemViewModel item, Uri url, string fileName)
    {
        if (!CanChangeLink(item)) return;
        var info = item.Info;
        TryDelete(info.TempPath, recycle: false);
        if (!ReturnToPage(info))
        {
            info.FinalUrl = null;
            info.Segments = [];
            info.TotalSize = null;
            info.ETag = null;
            info.LastModified = null;
        }
        info.Url = url.ToString();
        info.FileName = fileName;
        if (info.RequestedFileName is not null) info.RequestedFileName = fileName;
        info.CompletedUtc = null;
        info.Error = null;
        info.ErrorIsTransient = false;
        info.Status = DownloadStatus.Queued;
        store.Save(info);
        item.AddLog($"Link changed to {info.Url}");
        item.Refresh();
        LinkChanged?.Invoke(item);
    }

    /// <summary>
    /// Deletes files permanently, not to the Recycle Bin, so the space is freed (missing ones are skipped). For a
    /// batch's parts after extracting.
    /// </summary>
    public void DeleteFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths) TryDelete(path, recycle: false);
    }

    /// <summary>This download's own cap in bytes/second (0 = none). Applies at once if it is running.</summary>
    public void SetSpeedLimit(DownloadItemViewModel item, long bytesPerSecond)
    {
        if (item.Task is { } task) task.SetSpeedLimit(bytesPerSecond);
        else item.Info.SpeedLimit = bytesPerSecond > 0 ? bytesPerSecond : null;
        store.Save(item.Info);
        if (item.VideoJob is { } job && job.RateLimit != VideoJob.EffectiveLimit(bytesPerSecond, globalLimiter.BytesPerSecond))
            Restart(item);
    }

    /// <summary>Stops a running video and starts it again at once (--continue), e.g. to apply a new speed limit.</summary>
    private void Restart(DownloadItemViewModel item)
    {
        if (item.VideoJob is null) return;
        item.RestartRequested = true;
        item.Cts?.Cancel();
    }

    /// <summary>Removes downloads from the list. Partial files always go; finished files go to the Recycle Bin when asked.</summary>
    public async Task DeleteAsync(IReadOnlyList<DownloadItemViewModel> items, bool deleteFiles)
    {
        foreach (var item in items) Pause(item);
        await Task.WhenAll(items.Select(i => i.Run ?? Task.CompletedTask));

        foreach (var item in items)
        {
            store.Delete(item.Info.Id);
            Items.Remove(item);
            TryDelete(item.Info.TempPath, recycle: false);
            foreach (var leftover in VideoJob.LeftoverFiles(item.Info)) TryDelete(leftover, recycle: false);
            if (deleteFiles && item.Info.Status == DownloadStatus.Completed && !string.IsNullOrEmpty(item.Info.FileName))
                TryDelete(item.Info.FilePath, recycle: true);
        }
    }

    /// <summary>
    /// Pauses everything and saves it. Downloads that were running keep the Downloading status in the database,
    /// which is how <see cref="Load"/> knows to resume them next time.
    /// </summary>
    public void Shutdown()
    {
        // The browser step stops with TurboDL (the browser closes); those downloads come back Paused.
        foreach (var item in Items.Where(i => i.InBrowser).ToList())
        {
            item.BrowserCts?.Cancel();
            item.Info.Status = DownloadStatus.Paused;
            store.Save(item.Info);
        }

        var running = Items.Where(i => i.IsRunning).ToList();
        foreach (var item in running) Pause(item);

        // Wait for the engine tasks themselves (thread pool), not RunAsync, whose continuations need this thread.
        var engineTasks = running.Select(i => i.EngineRun).OfType<Task>().ToArray();
        try { Task.WaitAll(engineTasks, TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* paused or failed: both already checkpointed */ }

        foreach (var item in running.Where(i => i.Info.Status == DownloadStatus.Paused))
        {
            item.Info.Status = DownloadStatus.Downloading;
            store.Save(item.Info);
        }
    }

    private async Task RunAsync(DownloadItemViewModel item)
    {
        var info = item.Info;
        var task = new DownloadTask(info, http, globalLimiter: globalLimiter, hostThrottle: hostThrottle);
        task.Checkpoint += store.Save; // background thread; Save is thread-safe
        task.Log += message =>
        {
            log.LogDebug("[{Id}] {Message}", info.Id, message);
            _ui.BeginInvoke(() => item.AddLog(message));
        };

        var cts = new CancellationTokenSource();
        // Off the UI thread: the engine doesn't use ConfigureAwait(false), so its I/O would otherwise run here.
        var engineRun = Task.Run(() => task.RunAsync(cts.Token));
        item.Attach(task, cts, engineRun);

        Exception? error = null;
        try
        {
            await engineRun;
        }
        catch (OperationCanceledException)
        {
            // Paused. The engine already saved the Paused state.
        }
        catch (Exception ex)
        {
            error = ex;
            log.LogWarning(ex, "Download {Id} failed", info.Id);
        }
        finally
        {
            task.Checkpoint -= store.Save;
            item.Detach();
        }

        if (error is not null) item.AddLog(error.GetBaseException().Message);

        // A link that gave a host's web page instead of the file, added without the Add dialog's check (or before it
        // existed): the page isn't kept as the "file", and the browser is opened for it. Batch parts get this from BatchService.
        var savedHostPage = info.Status == DownloadStatus.Completed && info.BatchId is null && IsSavedHostPage(info);
        if (savedHostPage) MarkFailed(item, DownloadTask.HostPageMessage);

        // The host wants clicks (countdown, button) or a sign-in, or only lets browsers in: let the user do it in one.
        // Into the browser step before anyone hears the run ended, so the queue doesn't hand its slot to the next link.
        var toBrowser = (error is HostReturnedPageException || savedHostPage
                         || (info.ExpectFile && error?.GetBaseException() is ServerRefusedException { MayNeedBrowser: true }))
                        && EnterBrowser(item);

        Ended?.Invoke(item);
        if (info.Status == DownloadStatus.Completed)
            Completed?.Invoke(item);
        else if (error is RemoteFileChangedException && await prompts.ConfirmRestartAsync(info.FileName))
        {
            TryDelete(info.TempPath, recycle: false);
            info.Segments = [];
            info.TotalSize = null;
            info.ETag = null;
            info.LastModified = null;
            info.Error = null;
            store.Save(info);
            Start(item);
        }
        if (toBrowser) _ = RunBrowserAsync(item, userAsked: false);
    }

    /// <summary>Names that are meant to be markup: a download saved under one of these is the page the user wanted.</summary>
    private static readonly string[] PageExtensions = [".htm", ".html", ".xhtml", ".shtml", ".mht", ".mhtml", ".xml", ".svg"];

    /// <summary>The finished file is an HTML page although its name doesn't say so: a host's waiting page, not the file.</summary>
    private static bool IsSavedHostPage(DownloadInfo info) =>
        info.Kind == DownloadKind.Http
        && !PageExtensions.Any(ext => info.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        && FileSniffer.SniffFile(info.FilePath) == FileKind.Html;

    private async Task RunVideoAsync(DownloadItemViewModel item)
    {
        var info = item.Info;
        var resuming = info.Video?.Started == true;
        var job = new VideoJob(info, videoTools.Service, globalLimiter.BytesPerSecond);
        job.Checkpoint += store.Save; // yt-dlp's reader thread; Save is thread-safe

        var cts = new CancellationTokenSource();
        // A tools update may be replacing yt-dlp or Deno right now; start once it's done.
        var engineRun = Task.Run(async () =>
        {
            await videoTools.WaitForUpdateAsync();
            await job.RunAsync(cts.Token);
        });
        item.AttachVideo(job, cts, engineRun);
        item.AddLog($"{(resuming ? "Resuming" : "Starting")} with yt-dlp: {info.Video!.Describe()}" +
                    (job.RateLimit > 0 ? $", limited to {Format.Speed(job.RateLimit)}" : ""));

        Exception? error = null;
        try
        {
            await engineRun;
        }
        catch (OperationCanceledException)
        {
            // Paused: yt-dlp and ffmpeg were killed and VideoJob saved the Paused state; the .part files stay.
        }
        catch (Exception ex)
        {
            error = ex;
            log.LogWarning(ex, "Video download {Id} failed", info.Id);
        }
        finally
        {
            job.Checkpoint -= store.Save;
            item.Detach();
        }

        if (error is not null)
        {
            if (info.Status != DownloadStatus.Failed)
            {
                info.Status = DownloadStatus.Failed;
                info.Error = error.GetBaseException().Message;
                store.Save(info);
                item.Refresh();
            }
            item.AddLog(info.Error ?? error.Message);
        }

        if (item.RestartRequested)
        {
            item.RestartRequested = false;
            if (info.Status == DownloadStatus.Paused)
            {
                Start(item);
                return;
            }
        }

        Ended?.Invoke(item);
        if (info.Status == DownloadStatus.Completed)
        {
            item.AddLog($"Saved as {info.FilePath}");
            Completed?.Invoke(item);
        }
    }

    private void TryDelete(string path, bool recycle)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (recycle)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't delete {Path}", path);
        }
    }
}
