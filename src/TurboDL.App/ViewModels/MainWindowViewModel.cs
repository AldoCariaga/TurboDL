using System.Collections.ObjectModel;
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
    private readonly CategoryRules _categories;
    private readonly AddDownloadPrompt _addPrompt;
    private readonly IDeletePrompt _deletePrompt;
    private readonly SettingsService _settings;
    private readonly WindowService _windows;
    private readonly ClipboardWatcher _clipboard;
    private readonly DispatcherTimer _tick;
    private IReadOnlyList<DownloadItemViewModel> _selection = [];

    public MainWindowViewModel(DownloadManager manager, CategoryRules categories, AddDownloadPrompt addPrompt,
        IDeletePrompt deletePrompt, QueueService queue, SettingsService settings, WindowService windows,
        ClipboardWatcher clipboard)
    {
        _manager = manager;
        _categories = categories;
        _addPrompt = addPrompt;
        _deletePrompt = deletePrompt;
        _settings = settings;
        _windows = windows;
        _clipboard = clipboard;
        Queue = queue;

        Filters =
        [
            new("All", SymbolRegular.Apps24, _ => true),
            new("Downloading", SymbolRegular.ArrowDownload24,
                i => i.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Failed),
            new("Completed", SymbolRegular.CheckmarkCircle24, i => i.Status == DownloadStatus.Completed),
            new("Queued", SymbolRegular.Clock24, i => i.Status == DownloadStatus.Queued),
        ];
        RebuildCategories();
        SelectedFilter = Filters[0];
        categories.Changed += RebuildCategories;

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
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueService.IsRunning)) NotifyCommands();
        };
        settings.Changed += _ => UpdateSummary();

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(),
            Dispatcher.CurrentDispatcher);
        UpdateSummary();
    }

    public QueueService Queue { get; }
    public ObservableCollection<SidebarFilter> Filters { get; }
    public ICollectionView SidebarView { get; }
    public ICollectionView View { get; }

    [ObservableProperty] public partial SidebarFilter? SelectedFilter { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial string EmptyTitle { get; private set; } = "";
    [ObservableProperty] public partial string EmptyHint { get; private set; } = "";
    [ObservableProperty] public partial string StatusLine { get; private set; } = "";
    [ObservableProperty] public partial string TotalSpeed { get; private set; } = "";
    [ObservableProperty] public partial string SpeedLimitText { get; private set; } = "";

    /// <summary>One line for the tray icon's tooltip.</summary>
    [ObservableProperty] public partial string TrayText { get; private set; } = "TurboDL";

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

    [RelayCommand]
    private void PauseAll()
    {
        Queue.Stop();
        foreach (var item in _manager.Items.Where(i => i.IsRunning).ToList()) _manager.Pause(item);
    }

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

    /// <summary>Starts the queue, or stops it when it is running.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleQueue))]
    private void ToggleQueue()
    {
        if (Queue.IsRunning) Queue.Stop();
        else Queue.Start();
        NotifyCommands();
    }

    private bool CanToggleQueue() => Queue.IsRunning || _manager.Items.Any(i => i.Status == DownloadStatus.Queued);

    [RelayCommand]
    private void OpenSettings() => _windows.ShowSettings();

    [RelayCommand]
    private void ShowDetails(DownloadItemViewModel? item)
    {
        if (item is not null) _windows.ShowDetails(item);
    }

    /// <summary>Double-click or Enter: open a finished file, or show the details of an unfinished one.</summary>
    [RelayCommand]
    private void Activate(DownloadItemViewModel? item)
    {
        if (item is null) return;
        if (item.Status == DownloadStatus.Completed) Open(item);
        else _windows.ShowDetails(item);
    }

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
    private void CopyLink(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _clipboard.Ignore(item.Info.Url);
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
        var limit = _settings.Current.SpeedLimitKBps;
        SpeedLimitText = limit > 0 ? $"Limited to {Format.Speed(limit * 1024)}" : "";
        TrayText = active.Count > 0 ? $"TurboDL: {StatusLine}, {TotalSpeed}" : $"TurboDL{(StatusLine.Length > 0 ? ": " + StatusLine : "")}";

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
        ToggleQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Category entries follow the rules in Settings; the status views above them never change.</summary>
    private void RebuildCategories()
    {
        var selectedCategory = SelectedFilter is { Group: not null } f0 ? f0.Name : null;
        foreach (var old in Filters.Where(f => f.Group is not null).ToList()) Filters.Remove(old);
        foreach (var c in _categories.Categories)
            Filters.Add(new SidebarFilter(c, CategoryIcon(c), i => i.Category == c, "Categories"));
        if (selectedCategory is not null)
            SelectedFilter = Filters.FirstOrDefault(f => f.Group is not null && f.Name == selectedCategory) ?? Filters[0];
        UpdateSummaryIfReady();
    }

    private void UpdateSummaryIfReady()
    {
        if (View is not null) UpdateSummary();
    }

    private static SymbolRegular CategoryIcon(string category) => category switch
    {
        "Video" => SymbolRegular.Video24,
        "Music" => SymbolRegular.MusicNote224,
        "Documents" => SymbolRegular.DocumentText24,
        "Compressed" => SymbolRegular.FolderZip24,
        "Programs" => SymbolRegular.AppGeneric24,
        CategoryRules.Other => SymbolRegular.Document24,
        _ => SymbolRegular.Folder24,
    };
}
