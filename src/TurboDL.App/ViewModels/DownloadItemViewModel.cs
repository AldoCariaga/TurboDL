using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TurboDL.App.Services;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;
using TurboDL.Core.Video;

namespace TurboDL.App.ViewModels;

/// <summary>Where a download is in the browser step (a file host's page being clicked through).</summary>
public enum BrowserStage { None, Waiting, Open }

/// <summary>
/// One row in the list. Wraps the persisted <see cref="DownloadInfo"/> and, while it runs, its <see cref="DownloadTask"/>.
/// Values are pulled from the engine's snapshot by <see cref="Refresh"/> on the UI thread, never pushed from engine threads.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    public DownloadInfo Info { get; }

    internal DownloadTask? Task { get; private set; }

    /// <summary>The running yt-dlp job, for <see cref="DownloadKind.Video"/> items.</summary>
    internal VideoJob? VideoJob { get; private set; }

    /// <summary>Set to stop the running video job and start it again straight away (e.g. a new speed limit).</summary>
    internal bool RestartRequested { get; set; }
    internal CancellationTokenSource? Cts { get; private set; }
    /// <summary>The engine's own task (thread pool). Safe to block on during shutdown.</summary>
    internal Task? EngineRun { get; private set; }

    /// <summary>The manager's wrapper around <see cref="EngineRun"/>; completes after the UI has been updated.</summary>
    internal Task? Run { get; set; }

    public DownloadItemViewModel(DownloadInfo info)
    {
        Info = info;
        Refresh();
    }

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial ImageSource? Icon { get; private set; }
    [ObservableProperty] public partial DownloadStatus Status { get; private set; }
    [ObservableProperty] public partial string Category { get; private set; } = "";
    [ObservableProperty] public partial string SizeText { get; private set; } = Format.None;
    [ObservableProperty] public partial long? Total { get; private set; }
    [ObservableProperty] public partial double Fraction { get; private set; }
    [ObservableProperty] public partial string PercentText { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<SegmentState>? Segments { get; private set; }
    [ObservableProperty] public partial double BytesPerSecond { get; private set; }
    [ObservableProperty] public partial string SpeedText { get; private set; } = Format.None;
    [ObservableProperty] public partial string RemainingText { get; private set; } = Format.None;
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial string? Error { get; private set; }

    /// <summary>Engine messages (retries, splits) for the Details window; newest last, capped.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Log { get; } = [];

    internal void AddLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 200) Log.RemoveAt(0);
    }

    public bool IsRunning => Task is not null || VideoJob is not null;
    public bool IsVideo => Info.Kind == DownloadKind.Video;

    /// <summary>Waiting for the browser, or in it. Set by the manager only.</summary>
    internal BrowserStage Browser { get; private set; }

    /// <summary>Cancels the browser step (Pause); null outside it.</summary>
    internal CancellationTokenSource? BrowserCts { get; set; }

    /// <summary>The order this download joined the wait for the browser in.</summary>
    internal long BrowserTicket { get; set; }

    public bool InBrowser => Browser != BrowserStage.None;

    /// <summary>"Solving": its file host's page is being clicked through, or is waiting its turn in the browser.</summary>
    public bool IsSolving => InBrowser && !IsRunning;

    /// <summary>
    /// Running, or in its browser step: either way it holds one of the "downloads at once" slots, and Pause stops it.
    /// The browser step is part of getting the file, so a host page doesn't free the slot for the next link.
    /// </summary>
    public bool IsBusy => IsRunning || InBrowser;

    /// <summary>Video items: what was asked for, e.g. "Up to 1080p · mp4".</summary>
    public string VideoDescription => Info.Video?.Describe() ?? "";
    public bool CanResume => !IsBusy && Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued;
    public bool CanPause => IsBusy;

    internal void SetBrowser(BrowserStage stage)
    {
        Browser = stage;
        Refresh();
    }

    internal void Attach(DownloadTask task, CancellationTokenSource cts, Task engineRun)
    {
        Task = task;
        Cts = cts;
        EngineRun = engineRun;
        Refresh();
    }

    internal void AttachVideo(VideoJob job, CancellationTokenSource cts, Task engineRun)
    {
        VideoJob = job;
        Cts = cts;
        EngineRun = engineRun;
        Refresh();
    }

    internal void Detach()
    {
        Task = null;
        VideoJob = null;
        Cts?.Dispose();
        Cts = null;
        EngineRun = null;
        Refresh();
    }

    /// <summary>Pull the latest numbers from the running task (or from the saved info when idle).</summary>
    public void Refresh()
    {
        if (IsVideo)
        {
            RefreshVideo();
            return;
        }

        var task = Task;
        var snap = task?.GetSnapshot();

        // While a task runs the engine owns Info.Status; it can briefly read Queued/Paused before the first write.
        Status = task is not null && Info.Status is not (DownloadStatus.Completed or DownloadStatus.Failed)
            ? DownloadStatus.Downloading
            : Info.Status;

        if (Name != Info.FileName || Icon is null)
        {
            Name = string.IsNullOrEmpty(Info.FileName) ? Info.Url : Info.FileName;
            Icon = ShellIcons.ForFile(Info.FileName);
        }
        Category = Info.Category;
        Error = Info.Error;

        var total = snap?.Total ?? Info.TotalSize;
        var downloaded = snap?.Downloaded ?? Info.Downloaded;
        Total = total;
        Segments = snap?.Segments ?? Info.Segments;
        SizeText = total is not null ? Format.Bytes(total) : downloaded > 0 ? Format.Bytes(downloaded) : Format.None;

        Fraction = Status == DownloadStatus.Completed ? 1 : total is > 0 ? (double)downloaded / total.Value : 0;
        PercentText = Status == DownloadStatus.Completed || total is > 0 ? $"{Math.Floor(Fraction * 100):0}%" : "";

        BytesPerSecond = Status == DownloadStatus.Downloading ? snap?.BytesPerSecond ?? 0 : 0;
        SpeedText = Format.Speed(BytesPerSecond);
        RemainingText = Status == DownloadStatus.Downloading ? Format.Duration(snap?.Remaining) : Format.None;

        StatusText = Status switch
        {
            DownloadStatus.Downloading when snap is { ActiveConnections: 0, BytesPerSecond: < 1 } => "Connecting",
            DownloadStatus.Downloading => "Downloading",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => "Failed",
            _ => "",
        };
        if (IsSolving) StatusText = "Solving";

        RaiseStateChanged();
    }

    private void RefreshVideo()
    {
        var job = VideoJob;
        var snap = job?.GetSnapshot();
        Status = job is not null && Info.Status is not (DownloadStatus.Completed or DownloadStatus.Failed)
            ? DownloadStatus.Downloading
            : Info.Status;

        if (Name != Info.FileName || Icon is null)
        {
            Name = string.IsNullOrEmpty(Info.FileName) ? Info.Video?.Title ?? Info.Url : Info.FileName;
            Icon = ShellIcons.ForFile(string.IsNullOrEmpty(Info.FileName) ? "video.mp4" : Info.FileName);
        }
        Category = Info.Category;
        Error = Info.Error;
        Segments = null;

        var total = snap?.Total ?? Info.TotalSize;
        var downloaded = snap?.Downloaded ?? Info.Downloaded;
        Total = total;
        SizeText = total is not null
            ? (snap?.TotalIsEstimate == true ? "~" : "") + Format.Bytes(total)
            : downloaded > 0 ? Format.Bytes(downloaded) : Format.None;
        Fraction = Status == DownloadStatus.Completed ? 1 : total is > 0 ? Math.Clamp((double)downloaded / total.Value, 0, 1) : 0;
        PercentText = Status == DownloadStatus.Completed || total is > 0 ? $"{Math.Floor(Fraction * 100):0}%" : "";

        BytesPerSecond = Status == DownloadStatus.Downloading ? snap?.BytesPerSecond ?? 0 : 0;
        SpeedText = Format.Speed(BytesPerSecond);
        RemainingText = Status == DownloadStatus.Downloading ? Format.Duration(snap?.Remaining) : Format.None;

        StatusText = Status switch
        {
            DownloadStatus.Downloading => snap?.PhaseText is { } phase
                ? phase is "Merging" or "Processing" or "Starting" ? phase + "…" : phase
                : "Starting…",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => "Failed",
            _ => "",
        };

        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsSolving));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(VideoDescription));
    }
}
