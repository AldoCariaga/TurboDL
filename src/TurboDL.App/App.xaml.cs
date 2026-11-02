using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TurboDL.App.Services;
using TurboDL.App.ViewModels;
using TurboDL.App.Views;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;
using TurboDL.Core.Storage;

namespace TurboDL.App;

public partial class App : Application
{
    private const string MutexName = @"Local\TurboDL.SingleInstance";

    private IHost? _host;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per user: two copies would download into the same database and files.
        // The IPC pipe will later forward a second launch's arguments; for now just bring the first one forward.
        _singleInstance = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            ActivateRunningInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        var services = builder.Services;
        services.AddSingleton(_ => TurboHttp.CreateClient());
        services.AddSingleton(_ => new RateLimiter()); // global speed cap; 0 = unlimited until Settings exists
        services.AddSingleton(_ => new DownloadStore(builder.Configuration["db"])); // --db=<path> for a throwaway list
        services.AddSingleton(_ => new CategoryRules(System.IO.Path.Combine(KnownFolders.Downloads, "TurboDL")));
        services.AddSingleton<Prompts>();
        services.AddSingleton<IUserPrompts>(sp => sp.GetRequiredService<Prompts>());
        services.AddSingleton<IDeletePrompt>(sp => sp.GetRequiredService<Prompts>());
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<AddDownloadPrompt>();
        services.AddTransient<AddDownloadViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();

        TurboTheme.Initialize();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        _host.Services.GetRequiredService<DownloadManager>().Load();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.Services.GetRequiredService<DownloadManager>().Shutdown();
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _host.Dispose(); // also disposes the store and the HttpClient
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception");
        System.Windows.MessageBox.Show(e.Exception.Message, "TurboDL ran into a problem", MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void ActivateRunningInstance()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            using (p)
            {
                if (p.Id == self.Id || p.MainWindowHandle == 0) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);
}
