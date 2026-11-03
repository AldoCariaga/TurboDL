using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;
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
    private readonly DownloadStore store;
    private readonly IUserPrompts prompts;
    private readonly VideoToolsService videoTools;
    private readonly ILogger<DownloadManager> log;

    public DownloadManager(HttpClient http, RateLimiter globalLimiter, DownloadStore store, IUserPrompts prompts,
        VideoToolsService videoTools, SettingsService settings, ILogger<DownloadManager> log)
    {
        this.http = http;
        this.globalLimiter = globalLimiter;
        this.store = store;
        this.prompts = prompts;
        this.videoTools = videoTools;
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
        if (item.IsRunning || item.Info.Status == DownloadStatus.Completed) return;
        item.Run = item.IsVideo ? RunVideoAsync(item) : RunAsync(item);
    }

    /// <summary>Stops a download. A restart that was on its way (new speed limit) is called off: pausing wins.</summary>
    public void Pause(DownloadItemViewModel item)
    {
        item.RestartRequested = false;
        item.Cts?.Cancel();
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

    /// <summary>Sends files to the Recycle Bin (missing ones are skipped). For a batch's parts after extracting.</summary>
    public void RecycleFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths) TryDelete(path, recycle: true);
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
        var task = new DownloadTask(info, http, globalLimiter: globalLimiter);
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
    }

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
