using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TurboDL.App.Services;
using TurboDL.Core.Categories;
using TurboDL.Core.Model;
using Wpf.Ui.Controls;

namespace TurboDL.App.ViewModels;

/// <summary>Asks the user before deleting.</summary>
public interface IDeletePrompt
{
    /// <summary>Null = cancelled; otherwise whether to also delete the finished files.</summary>
    Task<bool?> ConfirmDeleteAsync(IReadOnlyList<DownloadItemViewModel> items);
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly AddDownloadPrompt _addPrompt;
    private readonly IDeletePrompt _deletePrompt;
    private readonly DispatcherTimer _tick;
    private IReadOnlyList<DownloadItemViewModel> _selection = [];

    public MainWindowViewModel(DownloadManager manager, CategoryRules categories, AddDownloadPrompt addPrompt, IDeletePrompt deletePrompt)
    {
        _manager = manager;
        _addPrompt = addPrompt;
        _deletePrompt = deletePrompt;

        Filters =
        [
            new("All", SymbolRegular.Apps24, _ => true),
            new("Downloading", SymbolRegular.ArrowDownload24,
                i => i.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Failed),
            new("Completed", SymbolRegular.CheckmarkCircle24, i => i.Status == DownloadStatus.Completed),
            new("Queued", SymbolRegular.Clock24, i => i.Status == DownloadStatus.Queued),
            .. categories.Categories.Select(c => new SidebarFilter(c, CategoryIcon(c), i => i.Category == c, "Categories")),
        ];
        SelectedFilter = Filters[0];

        var source = new ListCollectionView(manager.Items)
        {
            Filter = o => SelectedFilter?.Match((DownloadItemViewModel)o) ?? true,
            IsLiveFiltering = true,
        };
        source.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.Status));
        source.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.Category));
        // Newest first, like a browser's download list.
        source.CustomSort = Comparer<object>.Create((a, b) =>
            ((DownloadItemViewModel)b).Info.Id.CompareTo(((DownloadItemViewModel)a).Info.Id));
        View = source;

        var sidebar = new ListCollectionView(Filters);
        sidebar.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SidebarFilter.Group)));
        SidebarView = sidebar;

        manager.Items.CollectionChanged += (_, _) => UpdateSummary();
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(),
            Dispatcher.CurrentDispatcher);
        UpdateSummary();
    }

    public SidebarFilter[] Filters { get; }
    public ICollectionView SidebarView { get; }
    public ICollectionView View { get; }

    [ObservableProperty] public partial SidebarFilter? SelectedFilter { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial string EmptyTitle { get; private set; } = "";
    [ObservableProperty] public partial string EmptyHint { get; private set; } = "";
    [ObservableProperty] public partial string StatusLine { get; private set; } = "";
    [ObservableProperty] public partial string TotalSpeed { get; private set; } = "";

    partial void OnSelectedFilterChanged(SidebarFilter? value)
    {
        View?.Refresh();
        UpdateSummary();
    }

    public void Start() => _tick.Start();

    public void Stop() => _tick.Stop();

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void SetSelection(IReadOnlyList<DownloadItemViewModel> items)
    {
        _selection = items;
        NotifyCommands();
    }

    [RelayCommand]
    private void AddUrl()
    {
        _addPrompt.Show();
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        foreach (var item in _selection.Where(i => i.CanResume)) _manager.Start(item);
        NotifyCommands();
    }

    private bool CanResume() => _selection.Any(i => i.CanResume);

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        foreach (var item in _selection.Where(i => i.CanPause)) _manager.Pause(item);
    }

    private bool CanPause() => _selection.Any(i => i.CanPause);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var items = _selection.ToList();
        if (items.Count == 0) return;
        var deleteFiles = await _deletePrompt.ConfirmDeleteAsync(items);
        if (deleteFiles is null) return;
        await _manager.DeleteAsync(items, deleteFiles.Value);
    }

    private bool HasSelection() => _selection.Count > 0;

    [RelayCommand(CanExecute = nameof(HasQueued))]
    private void StartQueue()
    {
        _manager.StartQueue();
        NotifyCommands();
    }

    private bool HasQueued() => _manager.Items.Any(i => i.Status == DownloadStatus.Queued);

    /// <summary>Opens a finished file, or its folder with the file selected.</summary>
    [RelayCommand]
    private static void Open(DownloadItemViewModel? item)
    {
        if (item is null || item.Status != DownloadStatus.Completed || !File.Exists(item.Info.FilePath)) return;
        Process.Start(new ProcessStartInfo(item.Info.FilePath) { UseShellExecute = true });
    }

    [RelayCommand]
    private static void ShowInFolder(DownloadItemViewModel? item)
    {
        if (item is null) return;
        var path = item.Info.FilePath;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(item.Info.Directory))
            Process.Start(new ProcessStartInfo(item.Info.Directory) { UseShellExecute = true });
    }

    [RelayCommand]
    private static void CopyLink(DownloadItemViewModel? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Info.Url); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; nothing useful to do */ }
    }

    private void Tick()
    {
        foreach (var item in _manager.Items)
            if (item.IsRunning) item.Refresh();
        UpdateSummary();
        NotifyCommands();
    }

    private void UpdateSummary()
    {
        foreach (var f in Filters) f.Count = _manager.Items.Count(f.Match);

        var active = _manager.Items.Where(i => i.IsRunning).ToList();
        var queued = _manager.Items.Count(i => i.Status == DownloadStatus.Queued);
        var speed = active.Sum(i => i.BytesPerSecond);
        StatusLine = (active.Count, queued) switch
        {
            (0, 0) => _manager.Items.Count == 0 ? "" : "Nothing downloading",
            (0, _) => $"{queued} queued",
            (_, 0) => $"{active.Count} downloading",
            _ => $"{active.Count} downloading, {queued} queued",
        };
        TotalSpeed = active.Count > 0 ? Format.Speed(speed) : "";

        IsEmpty = SelectedFilter is null || SelectedFilter.Count == 0;
        (EmptyTitle, EmptyHint) = _manager.Items.Count == 0
            ? ("No downloads yet", "Copy a link, then choose Add URL.")
            : ($"Nothing in {SelectedFilter?.Name}", "Downloads show up here as their status changes.");
    }

    private void NotifyCommands()
    {
        ResumeCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    private static SymbolRegular CategoryIcon(string category) => category switch
    {
        "Video" => SymbolRegular.Video24,
        "Music" => SymbolRegular.MusicNote224,
        "Documents" => SymbolRegular.DocumentText24,
        "Compressed" => SymbolRegular.FolderZip24,
        "Programs" => SymbolRegular.AppGeneric24,
        _ => SymbolRegular.Document24,
    };
}
