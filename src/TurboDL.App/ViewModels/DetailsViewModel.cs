using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TurboDL.App.Services;
using TurboDL.Core.Model;

namespace TurboDL.App.ViewModels;

/// <summary>One part of the file in the Details window: its byte range, progress and current speed.</summary>
public sealed partial class SegmentRowViewModel : ObservableObject
{
    public required long Start { get; init; }
    [ObservableProperty] public partial int Number { get; set; }
    [ObservableProperty] public partial string RangeText { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial string PercentText { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = Format.None;
    [ObservableProperty] public partial bool IsMoving { get; set; }
}

public sealed partial class DetailsViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManager _manager;
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<long, (long Position, long Timestamp)> _last = [];
    private bool _loading;

    public DetailsViewModel(DownloadItemViewModel item, DownloadManager manager)
    {
        Item = item;
        _manager = manager;

        _loading = true;
        var limit = item.Info.SpeedLimit ?? 0;
        LimitEnabled = limit > 0;
        LimitKBps = limit > 0 ? limit / 1024 : 1024;
        _loading = false;

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Update(),
            Dispatcher.CurrentDispatcher);
        _tick.Start();
        Update();
    }

    public DownloadItemViewModel Item { get; }
    public ObservableCollection<SegmentRowViewModel> Parts { get; } = [];

    [ObservableProperty] public partial string DownloadedText { get; private set; } = "";
    [ObservableProperty] public partial string ConnectionsText { get; private set; } = "";
    [ObservableProperty] public partial string ResumeText { get; private set; } = "";
    [ObservableProperty] public partial bool LimitEnabled { get; set; }
    [ObservableProperty] public partial double? LimitKBps { get; set; }

    partial void OnLimitEnabledChanged(bool value) => ApplyLimit();
    partial void OnLimitKBpsChanged(double? value) => ApplyLimit();

    [RelayCommand]
    private void PauseOrResume()
    {
        if (Item.IsRunning) _manager.Pause(Item);
        else _manager.Start(Item);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var path = Item.Info.FilePath;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(Item.Info.Directory))
            Process.Start(new ProcessStartInfo(Item.Info.Directory) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _tick.Stop();
    }

    private void ApplyLimit()
    {
        if (_loading) return;
        var kbps = LimitEnabled ? (long)Math.Max(1, LimitKBps ?? 0) : 0;
        _manager.SetSpeedLimit(Item, kbps * 1024);
    }

    private void Update()
    {
        Item.Refresh(); // our own fresh snapshot, so per-part speeds use this timer's intervals
        var total = Item.Total;
        var segments = Item.Segments ?? [];
        var downloaded = segments.Sum(s => s.Position - s.Start);
        if (Item.Status == DownloadStatus.Completed && total is long t) downloaded = t;

        DownloadedText = total is long size
            ? $"{Format.Bytes(downloaded)} of {Format.Bytes(size)}"
            : Format.Bytes(downloaded);
        var moving = 0;
        var now = Stopwatch.GetTimestamp();

        // Parts appear as the engine splits; keep rows by start offset so they update in place.
        var ordered = segments.OrderBy(s => s.Start).ToList();
        if (Parts.Count != ordered.Count || Parts.Select(p => p.Start).Where((s, i) => s != ordered[i].Start).Any())
        {
            Parts.Clear();
            foreach (var s in ordered) Parts.Add(new SegmentRowViewModel { Start = s.Start });
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var row = Parts[i];
            var openEnded = s.End == long.MaxValue;
            var length = openEnded ? 0 : s.End - s.Start + 1;
            row.Number = i + 1;
            row.RangeText = openEnded
                ? $"From {Format.Bytes(s.Start)}"
                : $"{Format.Bytes(s.Start)} to {Format.Bytes(s.End + 1)}";
            row.Fraction = length > 0 ? Math.Clamp((double)(s.Position - s.Start) / length, 0, 1) : 0;
            row.PercentText = length > 0 ? $"{Math.Floor(row.Fraction * 100):0}%" : "";

            var speed = 0.0;
            if (_last.TryGetValue(s.Start, out var prev) && Item.IsRunning)
            {
                var seconds = Stopwatch.GetElapsedTime(prev.Timestamp, now).TotalSeconds;
                if (seconds > 0) speed = Math.Max(0, s.Position - prev.Position) / seconds;
            }
            _last[s.Start] = (s.Position, now);
            row.IsMoving = speed > 1;
            row.SpeedText = Format.Speed(speed);
            if (row.IsMoving) moving++;
        }

        ConnectionsText = Item.IsRunning
            ? $"{moving} of {Math.Max(1, Item.Info.SupportsRanges ? Item.Info.Connections : 1)} receiving data"
            : $"{Item.Info.Connections} when running";
        ResumeText = Item.Info.TotalSize is null && Item.Status != DownloadStatus.Completed
            ? "Not known until the download starts"
            : Item.Info.SupportsRanges ? "Yes" : "No, the server sends it in one piece";
    }
}
