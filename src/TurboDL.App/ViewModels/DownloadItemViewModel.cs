using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TurboDL.App.Services;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;

namespace TurboDL.App.ViewModels;

/// <summary>
/// One row in the list. Wraps the persisted <see cref="DownloadInfo"/> and, while it runs, its <see cref="DownloadTask"/>.
/// Values are pulled from the engine's snapshot by <see cref="Refresh"/> on the UI thread, never pushed from engine threads.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    public DownloadInfo Info { get; }

    internal DownloadTask? Task { get; private set; }
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

    public bool IsRunning => Task is not null;
    public bool CanResume => !IsRunning && Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued;
    public bool CanPause => IsRunning;

    internal void Attach(DownloadTask task, CancellationTokenSource cts, Task engineRun)
    {
        Task = task;
        Cts = cts;
        EngineRun = engineRun;
        Refresh();
    }

    internal void Detach()
    {
        Task = null;
        Cts?.Dispose();
        Cts = null;
        EngineRun = null;
        Refresh();
    }

    /// <summary>Pull the latest numbers from the running task (or from the saved info when idle).</summary>
    public void Refresh()
    {
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

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
    }
}
