using System.Windows;
using TurboDL.App.ViewModels;
using TurboDL.App.Views;

namespace TurboDL.App.Services;

/// <summary>Opens the secondary windows, one per download for Details and a single Settings window.</summary>
public sealed class WindowService(IServiceProvider services)
{
    private readonly Dictionary<long, DetailsWindow> _details = [];
    private SettingsWindow? _settings;

    /// <summary>Brings the main window back from the tray or from behind other windows.</summary>
    public static void ShowMain()
    {
        if (Application.Current.MainWindow is not { } main) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
        // Topmost toggle reliably raises the window when another app is in front.
        main.Topmost = true;
        main.Topmost = false;
        main.Focus();
    }

    public void ShowDetails(DownloadItemViewModel item)
    {
        if (_details.TryGetValue(item.Info.Id, out var open))
        {
            open.Activate();
            return;
        }
        var manager = (DownloadManager)services.GetService(typeof(DownloadManager))!;
        var window = new DetailsWindow(new DetailsViewModel(item, manager));
        PlaceNear(window);
        _details[item.Info.Id] = window;
        window.Closed += (_, _) => _details.Remove(item.Info.Id);
        window.Show();
    }

    public void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }
        var vm = new SettingsViewModel((SettingsService)services.GetService(typeof(SettingsService))!);
        _settings = new SettingsWindow(vm);
        PlaceNear(_settings);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private static void PlaceNear(Window window)
    {
        if (Application.Current.MainWindow is { IsVisible: true } main)
        {
            window.Owner = main;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
