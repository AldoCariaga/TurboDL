using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using TurboDL.Installer;
using Wpf.Ui.Appearance;

namespace TurboDL.Setup;

/// <summary>
/// Routes the command line: the elevated worker, an uninstall that has to move out of the install folder first,
/// silent runs, or the window. Exit codes: 0 done, 1 failed, 2 TurboDL still running, 3 admin prompt declined,
/// 4 wrong account, 5 bad command line or a build without TurboDL's files.
/// </summary>
public partial class App : Application
{
    public const int ExitFailed = 1, ExitAppRunning = 2, ExitDeclined = 3, ExitWrongAccount = 4, ExitUsage = 5;

    private bool _cleanUpOnExit = true;
    private string? _relocatedFolder;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetupArgs args;
        try
        {
            args = SetupArgs.Parse(e.Args);
        }
        catch (ArgumentException ex)
        {
            // A worker shares its unpack folder with the UI that started it; that one cleans up.
            var worker = e.Args.Any(a => a.TrimStart().StartsWith("/worker", StringComparison.OrdinalIgnoreCase));
            _cleanUpOnExit = !worker;
            Fail(SetupArgs.AsksForNoWindow(e.Args), SetupArgs.RawLog(e.Args), worker ? "error|" + ex.Message : ex.Message, ExitUsage);
            return;
        }

        if (args.Worker is not null)
        {
            // The UI that started this one is still running from the same unpack folder; it cleans up.
            _cleanUpOnExit = false;
            Shutdown(Worker.Run(SetupContext.Create(args)));
            return;
        }

        if (args.Uninstall && !args.Relocated && RelocateUninstall(args, e.Args))
        {
            _cleanUpOnExit = false; // the copy uses the same unpack folder and cleans it when it's done
            Shutdown(0);
            return;
        }
        if (args.Relocated) _relocatedFolder = Path.GetDirectoryName(Environment.ProcessPath);

        if (SessionUser.WrongAccount() is { } wrongAccount)
        {
            Fail(args.Silent, args.Log, wrongAccount, ExitWrongAccount);
            return;
        }

        var context = SetupContext.Create(args);
        if (!args.Uninstall && !SetupContext.HasPayload)
        {
            Fail(args.Silent, args.Log, "This Setup was built without TurboDL's files. Build it with scripts\\publish.ps1.", ExitUsage);
            return;
        }

        if (args.Silent)
        {
            // Anything unexpected ends a silent run with a log line and an exit code, never a crash dialog.
            DispatcherUnhandledException += (_, ex) =>
            {
                ex.Handled = true;
                WriteLog(args.Log, "Failed: " + ex.Exception.Message);
                Shutdown(ExitFailed);
            };
            Dispatcher.InvokeAsync(async () => Shutdown(await RunSilentAsync(context)));
            return;
        }

        ApplyTheme();
        var window = new MainWindow(context);
        window.Closed += (_, _) => Shutdown(window.ExitCode);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_cleanUpOnExit)
        {
            try
            {
                SessionUser.CleanUpAfterExit([SessionUser.OwnExtractFolder(InstallLocations.DefaultExtractDir), _relocatedFolder]);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                // A few MB left in %TEMP% is no reason to fail Setup.
            }
        }
        base.OnExit(e);
    }

    private static async Task<int> RunSilentAsync(SetupContext context)
    {
        var args = context.Args;
        var flow = new SetupFlow(context);
        FlowResult result;
        if (args.Uninstall)
        {
            result = await flow.UninstallAsync(args.RemoveData, null);
        }
        else
        {
            var current = context.Engine.CurrentOptions();
            var options = new InstallOptions(
                args.StartMenu ?? current.StartMenuShortcut,
                args.Desktop ?? current.DesktopShortcut,
                args.Startup ?? current.StartWithWindows);
            result = await flow.InstallAsync(options, null);
        }

        WriteLog(args.Log, result.Outcome == Outcome.Done ? "ok" : $"{result.Outcome}: {result.Message}");
        return result.Outcome switch
        {
            Outcome.Done => 0,
            Outcome.AppRunning => ExitAppRunning,
            Outcome.Declined => ExitDeclined,
            _ => ExitFailed,
        };
    }

    /// <summary>
    /// Installed apps starts the copy of Setup inside the install folder, which can't delete itself. So it copies
    /// itself to %TEMP%, starts that with /relocated and exits; the copy is deleted when it finishes.
    /// </summary>
    private static bool RelocateUninstall(SetupArgs args, string[] rawArgs)
    {
        var self = Environment.ProcessPath;
        var installDir = SetupContext.Create(args).Locations.InstallDir;
        if (self is null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(self)), installDir, StringComparison.OrdinalIgnoreCase))
            return false;

        var folder = Path.Combine(Path.GetTempPath(), $"TurboDL-Uninstall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Product.SetupExe);
        File.Copy(self, copy);

        var start = new ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (var arg in rawArgs) start.ArgumentList.Add(arg);
        if (args.InstallDir is null) start.ArgumentList.Add($"/dir={installDir}");
        start.ArgumentList.Add("/relocated");
        Process.Start(start)?.Dispose();
        return true;
    }

    /// <summary>Silent and worker runs never show a window: they log (when /log is given) and exit with the code.</summary>
    private void Fail(bool noWindow, string? log, string message, int exitCode)
    {
        if (noWindow) WriteLog(log, message.StartsWith("error|") ? message : "Failed: " + message);
        else MessageBox.Show(message, "TurboDL Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown(exitCode);
    }

    private static void WriteLog(string? log, string line)
    {
        if (log is null) return;
        try { File.AppendAllText(log, line + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The system's light or dark theme with TurboDL's teal, like the app.</summary>
    private static void ApplyTheme()
    {
        ApplicationThemeManager.ApplySystemTheme(false);
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        Color[] ramp = dark
            ? [Hex(0x2FA6A9), Hex(0x3DB4B6), Hex(0x4CC2C4), Hex(0x73D3D4)]
            : [Hex(0x14939E), Hex(0x11878F), Hex(0x0E7C86), Hex(0x0A6770)];
        ApplicationAccentColorManager.Apply(ramp[0], ramp[1], ramp[2], ramp[3]);
    }

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
