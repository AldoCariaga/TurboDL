using TurboDL.Core.Categories;

namespace TurboDL.Core.Settings;

public enum ThemeMode { System, Light, Dark }

/// <summary>What to do once the queue has nothing left to download.</summary>
public enum WhenQueueDone { Nothing, Sleep, ShutDown, ExitApp }

public sealed class CategoryRule
{
    public string Name { get; set; } = "";

    /// <summary>Space- or comma-separated extensions, e.g. "zip rar 7z".</summary>
    public string Extensions { get; set; } = "";

    public string[] ExtensionList() =>
        Extensions.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();
}

public sealed class ScheduleSettings
{
    public bool StartEnabled { get; set; }
    public TimeOnly StartAt { get; set; } = new(2, 0);
    public bool StopEnabled { get; set; }
    public TimeOnly StopAt { get; set; } = new(7, 0);

    /// <summary>Days the schedule runs on. Empty means every day.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    public WhenQueueDone WhenDone { get; set; } = WhenQueueDone.Nothing;
}

/// <summary>Everything on the Settings page, saved as JSON next to the database.</summary>
public sealed class AppSettings
{
    /// <summary>Category folders go under here. Empty = Downloads\TurboDL.</summary>
    public string DownloadFolder { get; set; } = "";

    public int DefaultConnections { get; set; } = 8;
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>Global cap in KB/s across all downloads. 0 = unlimited.</summary>
    public long SpeedLimitKBps { get; set; }

    public List<CategoryRule> Categories { get; set; } =
        CategoryRules.Defaults.Select(d => new CategoryRule { Name = d.Category, Extensions = string.Join(' ', d.Extensions) }).ToList();

    /// <summary>Offer to download links copied to the clipboard.</summary>
    public bool WatchClipboard { get; set; } = true;

    /// <summary>File types the clipboard watcher (and later the browser) hands to TurboDL.</summary>
    public string CatchExtensions { get; set; } =
        "zip rar 7z tar gz iso exe msi msix apk mp4 mkv webm avi mov mp3 flac m4a wav pdf epub";

    /// <summary>Browser downloads smaller than this stay in the browser. 0 = catch everything.</summary>
    public long MinCatchSizeKB { get; set; }

    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public ScheduleSettings Schedule { get; set; } = new();

    /// <summary>Remembered so a queue that was running when the app closed carries on next time.</summary>
    public bool QueueRunning { get; set; }

    public string[] CatchExtensionList() => new CategoryRule { Extensions = CatchExtensions }.ExtensionList();
}
