using CommunityToolkit.Mvvm.ComponentModel;
using TurboDL.Core.Model;

namespace TurboDL.App.ViewModels;

/// <summary>One batch in the sidebar: its name, how many parts are done, and a one-line status.</summary>
public sealed partial class BatchViewModel(BatchInfo info) : ObservableObject
{
    public BatchInfo Info { get; } = info;

    [ObservableProperty] public partial string Name { get; private set; } = info.Name;
    [ObservableProperty] public partial int Total { get; private set; }
    [ObservableProperty] public partial int Completed { get; private set; }
    [ObservableProperty] public partial int Failed { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    /// <summary>True while 7-Zip is being set up or is extracting.</summary>
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    /// <summary>Recounts the parts and rewrites the status line. <paramref name="busyText"/> replaces "Extracting…" while extracting.</summary>
    public void Refresh(IReadOnlyList<DownloadItemViewModel> items, string? busyText = null)
    {
        Name = Info.Name;
        Total = items.Count;
        Completed = items.Count(i => i.Status == DownloadStatus.Completed);
        Failed = items.Count(i => i.Status == DownloadStatus.Failed);
        IsBusy = Info.Status == BatchStatus.Extracting;
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
