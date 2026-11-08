using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace TurboDL.App.ViewModels;

/// <summary>One entry in the sidebar: a status view (All, Downloading…), a batch or a category.</summary>
public sealed partial class SidebarFilter(string name, SymbolRegular icon, Func<DownloadItemViewModel, bool> match, string? group = null)
    : ObservableObject
{
    public string Name { get; } = name;
    public SymbolRegular Icon { get; } = icon;

    /// <summary>Sidebar section heading; null for the status views at the top.</summary>
    public string? Group { get; } = group;

    public Func<DownloadItemViewModel, bool> Match { get; } = match;

    /// <summary>The batch this entry shows; null for status views and categories.</summary>
    public long? BatchId { get; init; }

    [ObservableProperty] public partial int Count { get; set; }

    /// <summary>A second line under the name (a batch's status); null for none.</summary>
    [ObservableProperty] public partial string? Subtitle { get; set; }

    /// <summary>A batch's extraction progress (0–1) for the thin bar under its status; null hides the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressFraction))]
    public partial double? Progress { get; set; }

    public bool HasProgress => Progress is not null;
    public double ProgressFraction => Progress ?? 0;
}
