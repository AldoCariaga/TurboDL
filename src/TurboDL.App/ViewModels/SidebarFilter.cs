using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace TurboDL.App.ViewModels;

/// <summary>One entry in the sidebar: a status view (All, Downloading…) or a category.</summary>
public sealed partial class SidebarFilter(string name, SymbolRegular icon, Func<DownloadItemViewModel, bool> match, string? group = null)
    : ObservableObject
{
    public string Name { get; } = name;
    public SymbolRegular Icon { get; } = icon;

    /// <summary>Sidebar section heading; null for the status views at the top.</summary>
    public string? Group { get; } = group;

    public Func<DownloadItemViewModel, bool> Match { get; } = match;

    [ObservableProperty] public partial int Count { get; set; }
}
