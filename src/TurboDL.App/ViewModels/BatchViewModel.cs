using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TurboDL.Core.Model;

namespace TurboDL.App.ViewModels;

/// <summary>One batch in the sidebar: its name, how many parts are done, a one-line status, extraction progress and a log.</summary>
public sealed partial class BatchViewModel(BatchInfo info) : ObservableObject
{
    public BatchInfo Info { get; } = info;

    /// <summary>How far the archive being extracted has got (0–1); null when nothing is being extracted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressFraction), nameof(ProgressText))]
    public partial double? Progress { get; private set; }

    public bool HasProgress => Progress is not null;
    public double ProgressFraction => Progress ?? 0;
    public string ProgressText => Progress is { } p ? $"{p * 100:0}%" : "";

    /// <summary>"40/52 parts · 1 failed" for the details window.</summary>
    public string PartsText => Failed > 0 ? $"{Completed}/{Total} · {Failed} failed" : $"{Completed}/{Total}";

    /// <summary>What the batch did, newest last: extraction, retries, parts the browser solved. Kept while TurboDL runs.</summary>
    public ObservableCollection<string> Log { get; } = [];

    internal void AddLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 500) Log.RemoveAt(0);
    }

    internal void SetProgress(double? fraction) => Progress = fraction;

    [ObservableProperty] public partial string Name { get; private set; } = info.Name;
    [ObservableProperty] public partial int Total { get; private set; }
    [ObservableProperty] public partial int Completed { get; private set; }
    [ObservableProperty] public partial int Failed { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    /// <summary>True while 7-Zip is being set up or is extracting.</summary>
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    /// <summary>Where the batch extracts, for the details window: the folder chosen, or the default one.</summary>
    [ObservableProperty] public partial string ExtractToText { get; private set; } = "";

    /// <summary>Whether the parts are deleted permanently once their archive has extracted.</summary>
    [ObservableProperty] public partial bool DeleteParts { get; private set; }

    /// <summary>Recounts the parts and rewrites the status line. <paramref name="busyText"/> replaces "Extracting…" while extracting.</summary>
    public void Refresh(IReadOnlyList<DownloadItemViewModel> items, string? busyText = null)
    {
        Name = Info.Name;
        Total = items.Count;
        Completed = items.Count(i => i.Status == DownloadStatus.Completed);
        Failed = items.Count(i => i.Status == DownloadStatus.Failed);
        IsBusy = Info.Status == BatchStatus.Extracting;
        if (!IsBusy) Progress = null;
        ExtractToText = Info.ExtractTo ?? System.IO.Path.Combine(Info.Directory, "Extracted") + "  (default)";
        DeleteParts = Info.DeleteParts;
        OnPropertyChanged(nameof(PartsText));
        StatusText = Info.Status switch
        {
            BatchStatus.Downloading => $"{Completed}/{Total} parts" + (Failed > 0 ? $" · {Failed} failed" : ""),
            BatchStatus.Extracting => busyText ?? "Extracting…",
            BatchStatus.Extracted => "Extracted",
            BatchStatus.Done => "Done",
            _ => Info.Message ?? "",
        };
    }
}
