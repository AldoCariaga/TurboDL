using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TurboDL.App.Services;
using TurboDL.Core.Categories;
using TurboDL.Core.Settings;
using ThemeMode = TurboDL.Core.Settings.ThemeMode;

namespace TurboDL.App.ViewModels;

public sealed partial class CategoryRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Extensions { get; set; } = "";
}

public sealed partial class DayToggle(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Label { get; } = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
    [ObservableProperty] public partial bool IsOn { get; set; }
}

public sealed record Choice<T>(T Value, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AppSettings _edit;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        _edit = settings.Edit();

        DownloadFolder = settings.DownloadFolder;
        DefaultConnections = _edit.DefaultConnections;
        MaxConcurrent = _edit.MaxConcurrent;
        SpeedLimitEnabled = _edit.SpeedLimitKBps > 0;
        SpeedLimitKBps = _edit.SpeedLimitKBps > 0 ? _edit.SpeedLimitKBps : 2048;

        var s = _edit.Schedule;
        TimeChoices = Enumerable.Range(0, 96).Select(i => new TimeOnly(i / 4, i % 4 * 15)).ToList();
        foreach (var t in new[] { s.StartAt, s.StopAt }.Where(t => !TimeChoices.Contains(t))) TimeChoices.Add(t);
        TimeChoices.Sort();
        ScheduleStart = s.StartEnabled;
        StartAt = s.StartAt;
        ScheduleStop = s.StopEnabled;
        StopAt = s.StopAt;
        var firstDay = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        Days = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDay + i) % 7))
            .Select(d => new DayToggle(d) { IsOn = s.Days.Count == 0 || s.Days.Contains(d) }).ToList();
        WhenDone = s.WhenDone;

        Categories = new(_edit.Categories.Select(c => new CategoryRowViewModel { Name = c.Name, Extensions = c.Extensions }));

        WatchClipboard = _edit.WatchClipboard;
        CatchExtensions = _edit.CatchExtensions;
        MinCatchSizeKB = _edit.MinCatchSizeKB;

        Theme = _edit.Theme;
        CloseToTray = _edit.CloseToTray;
        NotifyOnComplete = _edit.NotifyOnComplete;
        StartWithWindows = StartupRegistration.IsEnabled();
    }

    public List<TimeOnly> TimeChoices { get; }
    public IReadOnlyList<DayToggle> Days { get; }
    public ObservableCollection<CategoryRowViewModel> Categories { get; }

    public IReadOnlyList<Choice<WhenQueueDone>> WhenDoneChoices { get; } =
        Enum.GetValues<WhenQueueDone>().Select(v => new Choice<WhenQueueDone>(v, WhenDoneText.Describe(v))).ToList();

    public IReadOnlyList<Choice<ThemeMode>> ThemeChoices { get; } =
    [
        new(ThemeMode.System, "Use Windows setting"),
        new(ThemeMode.Light, "Light"),
        new(ThemeMode.Dark, "Dark"),
    ];

    [ObservableProperty] public partial string DownloadFolder { get; set; } = "";
    [ObservableProperty] public partial double? DefaultConnections { get; set; }
    [ObservableProperty] public partial double? MaxConcurrent { get; set; }
    [ObservableProperty] public partial bool SpeedLimitEnabled { get; set; }
    [ObservableProperty] public partial double? SpeedLimitKBps { get; set; }

    [ObservableProperty] public partial bool ScheduleStart { get; set; }
    [ObservableProperty] public partial TimeOnly StartAt { get; set; }
    [ObservableProperty] public partial bool ScheduleStop { get; set; }
    [ObservableProperty] public partial TimeOnly StopAt { get; set; }
    [ObservableProperty] public partial WhenQueueDone WhenDone { get; set; }

    [ObservableProperty] public partial bool WatchClipboard { get; set; }
    [ObservableProperty] public partial string CatchExtensions { get; set; } = "";
    [ObservableProperty] public partial double? MinCatchSizeKB { get; set; }

    [ObservableProperty] public partial ThemeMode Theme { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool NotifyOnComplete { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }

    [ObservableProperty] public partial string Error { get; private set; } = "";

    public event Action? CloseRequested;

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where downloads go" };
        if (Directory.Exists(DownloadFolder)) dialog.InitialDirectory = DownloadFolder;
        if (dialog.ShowDialog() == true) DownloadFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void AddCategory() => Categories.Add(new CategoryRowViewModel());

    [RelayCommand]
    private void RemoveCategory(CategoryRowViewModel? row)
    {
        if (row is not null) Categories.Remove(row);
    }

    [RelayCommand]
    private void ResetCategories()
    {
        Categories.Clear();
        foreach (var (name, exts) in CategoryRules.Defaults)
            Categories.Add(new CategoryRowViewModel { Name = name, Extensions = string.Join(' ', exts) });
    }

    [RelayCommand]
    private void Save()
    {
        if (Validate() is { } problem)
        {
            Error = problem;
            return;
        }

        var folder = DownloadFolder.Trim();
        _edit.DownloadFolder = string.Equals(folder, SettingsService.DefaultDownloadFolder, StringComparison.OrdinalIgnoreCase) ? "" : folder;
        _edit.DefaultConnections = (int)(DefaultConnections ?? 8);
        _edit.MaxConcurrent = (int)(MaxConcurrent ?? 3);
        _edit.SpeedLimitKBps = SpeedLimitEnabled ? (long)Math.Max(1, SpeedLimitKBps ?? 0) : 0;

        _edit.Schedule.StartEnabled = ScheduleStart;
        _edit.Schedule.StartAt = StartAt;
        _edit.Schedule.StopEnabled = ScheduleStop;
        _edit.Schedule.StopAt = StopAt;
        _edit.Schedule.Days = Days.All(d => d.IsOn) ? [] : Days.Where(d => d.IsOn).Select(d => d.Day).ToList();
        _edit.Schedule.WhenDone = WhenDone;

        _edit.Categories = Categories
            .Where(c => c.Name.Trim().Length > 0)
            .Select(c => new CategoryRule { Name = c.Name.Trim(), Extensions = c.Extensions.Trim() })
            .ToList();

        _edit.WatchClipboard = WatchClipboard;
        _edit.CatchExtensions = CatchExtensions.Trim();
        _edit.MinCatchSizeKB = (long)Math.Max(0, MinCatchSizeKB ?? 0);

        _edit.Theme = Theme;
        _edit.CloseToTray = CloseToTray;
        _edit.NotifyOnComplete = NotifyOnComplete;
        _edit.StartWithWindows = StartWithWindows;
        _edit.QueueRunning = _settings.Current.QueueRunning; // runtime state, not edited here

        try
        {
            if (StartWithWindows != StartupRegistration.IsEnabled()) StartupRegistration.Set(StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Error = $"Couldn't change Start with Windows: {ex.Message}";
            return;
        }

        _settings.Apply(_edit);
        TurboTheme.Set(Theme);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private string? Validate()
    {
        var folder = DownloadFolder.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
            return "Choose a full folder path for downloads, like C:\\Users\\you\\Downloads\\TurboDL.";
        if (Days.All(d => !d.IsOn) && (ScheduleStart || ScheduleStop))
            return "Pick at least one day for the schedule.";
        var names = Categories.Select(c => c.Name.Trim()).Where(n => n.Length > 0).ToList();
        var dup = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) return $"There are two categories called {dup.Key}. Rename or remove one.";
        if (names.Any(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return "Category names become folder names, so they can't contain \\ / : * ? \" < > |";
        return null;
    }
}
