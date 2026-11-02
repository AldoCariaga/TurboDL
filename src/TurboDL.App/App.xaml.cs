using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TurboDL.App.Services;
using TurboDL.App.ViewModels;
using TurboDL.App.Views;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;
using TurboDL.Core.Settings;
using TurboDL.Core.Storage;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace TurboDL.App;

public partial class App : Application
{
    private const string MutexName = @"Local\TurboDL.SingleInstance";

    /// <summary>Broadcast by a second launch so the running copy shows its window (even from the tray).</summary>
    public static readonly int ShowWindowMessage = RegisterWindowMessage("TurboDL.ShowWindow");

    private IHost? _host;
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private LinkPromptWindow? _linkPrompt;
    private DownloadItemViewModel? _lastFinished;
    private bool _exiting;
    private bool _toldAboutTray;

    private T Get<T>() where T : notnull => _host!.Services.GetRequiredService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per user: two copies would download into the same database and files.
        _singleInstance = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            PostMessage(HWND_BROADCAST, ShowWindowMessage, 0, 0);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // the tray keeps the app alive with no window open

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        // --db=<path> runs on a throwaway list; its settings then live next to it.
        var db = builder.Configuration["db"];
        var settingsPath = db is null ? null : Path.ChangeExtension(db, ".settings.json");

        var services = builder.Services;
        services.AddSingleton(_ => TurboHttp.CreateClient());
        services.AddSingleton(_ => new RateLimiter());
        services.AddSingleton(_ => new DownloadStore(db));
        services.AddSingleton(_ => new SettingsStore(settingsPath));
        services.AddSingleton(_ => new CategoryRules(SettingsService.DefaultDownloadFolder));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<Prompts>();
        services.AddSingleton<IUserPrompts>(sp => sp.GetRequiredService<Prompts>());
        services.AddSingleton<IDeletePrompt>(sp => sp.GetRequiredService<Prompts>());
        services.AddSingleton<IPowerActions, PowerActions>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<QueueService>();
        services.AddSingleton<ClipboardWatcher>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<AddDownloadPrompt>();
        services.AddTransient<AddDownloadViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();

        var settings = Get<SettingsService>();
        TurboTheme.Initialize(settings.Current.Theme);

        var window = Get<MainWindow>();
        MainWindow = window;
        window.Closing += OnMainWindowClosing;

        var manager = Get<DownloadManager>();
        manager.Completed += OnDownloadFinished;
        var queue = Get<QueueService>();
        queue.Finished += OnQueueFinished;

        var clipboard = Get<ClipboardWatcher>();
        clipboard.Attach(window);
        clipboard.LinkCopied += ShowLinkPrompt;

        CreateTray();

        // Started by Windows at sign-in: stay in the tray.
        if (!e.Args.Contains("--tray")) window.Show();

        manager.Load();
        queue.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            Get<DownloadManager>().Shutdown();
            Get<ClipboardWatcher>().Dispose();
            _tray?.Dispose();
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _host.Dispose(); // also disposes the store and the HttpClient
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Quit for real: from the tray menu, or when closing isn't set to keep TurboDL in the tray.</summary>
    public void RequestExit()
    {
        _exiting = true;
        Shutdown();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (!Get<SettingsService>().Current.CloseToTray)
        {
            RequestExit();
            return;
        }

        e.Cancel = true;
        MainWindow?.Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray?.Notify("TurboDL is still running", "Downloads carry on in the background. Open TurboDL from its icon here.");
        }
    }

    private void CreateTray()
    {
        var vm = Get<MainWindowViewModel>();
        var queue = Get<QueueService>();

        var queueItem = new MenuItem { Icon = new SymbolIcon(SymbolRegular.TextBulletListLtr24) };
        queueItem.Click += (_, _) =>
        {
            if (queue.IsRunning) queue.Stop();
            else queue.Start();
        };

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open TurboDL", SymbolRegular.Window24, WindowService.ShowMain, bold: true));
        menu.Items.Add(Item("Add URL", SymbolRegular.Add24, () => vm.AddUrlCommand.Execute(null)));
        menu.Items.Add(queueItem);
        menu.Items.Add(Item("Pause all", SymbolRegular.Pause24, () => vm.PauseAllCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", SymbolRegular.Power24, RequestExit));
        menu.Opened += (_, _) => queueItem.Header = queue.IsRunning ? "Stop queue" : "Start queue";

        _tray = new TrayIcon { Menu = menu };
        _tray.Click += WindowService.ShowMain;
        _tray.NotificationClick += () =>
        {
            if (_lastFinished is { } item) vm.ShowInFolderCommand.Execute(item);
            else WindowService.ShowMain();
        };
        _tray.Show();
        _tray.SetTooltip(vm.TrayText);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.TrayText)) _tray.SetTooltip(vm.TrayText);
        };

        static MenuItem Item(string header, SymbolRegular icon, Action action, bool bold = false)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon(icon) };
            if (bold) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void OnDownloadFinished(DownloadItemViewModel item)
    {
        _lastFinished = item;
        if (Get<SettingsService>().Current.NotifyOnComplete)
            _tray?.Notify("Download finished", $"{item.Name}\n{Format.Bytes(item.Total)}. Click to show it in its folder.");
    }

    private void OnQueueFinished(WhenQueueDone action)
    {
        if (action == WhenQueueDone.Nothing)
        {
            if (Get<SettingsService>().Current.NotifyOnComplete)
                _tray?.Notify("Queue finished", "Everything in the queue has downloaded.");
            return;
        }

        var countdown = new CountdownWindow(WhenDoneText.Describe(action));
        countdown.Closed += (_, _) =>
        {
            if (!countdown.Proceed) return;
            var power = Get<IPowerActions>();
            switch (action)
            {
                case WhenQueueDone.ExitApp:
                    RequestExit();
                    break;
                case WhenQueueDone.Sleep:
                    power.Sleep();
                    break;
                case WhenQueueDone.ShutDown:
                    power.ShutDown(); // Windows then asks the app to close, which saves everything
                    break;
            }
        };
        countdown.Show();
        countdown.Activate();
    }

    private void ShowLinkPrompt(Uri link)
    {
        _linkPrompt?.Close();
        var prompt = new LinkPromptWindow(link);
        prompt.Closed += (_, _) =>
        {
            if (ReferenceEquals(_linkPrompt, prompt)) _linkPrompt = null;
            if (prompt.Accepted) Get<AddDownloadPrompt>().Show(link.ToString());
        };
        _linkPrompt = prompt;
        prompt.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception");
        System.Windows.MessageBox.Show(e.Exception.Message, "TurboDL ran into a problem", System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    private const nint HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
