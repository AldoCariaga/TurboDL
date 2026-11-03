using System.Diagnostics;
using TurboDL.Installer;

namespace TurboDL.Setup;

public enum Outcome
{
    Done,

    /// <summary>TurboDL is still running from the install folder after being asked to quit.</summary>
    AppRunning,

    /// <summary>The Windows admin prompt was declined; nothing changed.</summary>
    Declined,

    Failed,
}

public sealed record FlowResult(Outcome Outcome, string? Message = null)
{
    public static FlowResult Ok { get; } = new(Outcome.Done);
}

/// <summary>An install or uninstall from start to end, the same for the window and for /S.</summary>
public sealed class SetupFlow(SetupContext context)
{
    public static TimeSpan QuitTimeout { get; } = TimeSpan.FromSeconds(10);

    public const string CloseTurboDL = "TurboDL is running. Close it (right-click its tray icon > Exit), then click Retry.";

    public async Task<FlowResult> InstallAsync(InstallOptions options, IProgress<InstallProgress>? progress)
    {
        // Machine half 3 steps + user half 4 steps.
        const int total = 7;
        progress?.Report(new(0, total, "Closing TurboDL"));
        if (!await RunningApp.QuitAsync(context.Locations.InstallDir, QuitTimeout)) return new(Outcome.AppRunning, CloseTurboDL);

        var error = await Worker.RunAsync(context, "install", Offset(progress, 0, total));
        if (error is not null) return error == Worker.Declined ? new(Outcome.Declined, error) : new(Outcome.Failed, error);

        try
        {
            context.Engine.InstallUser(options, Offset(progress, 3, total), SessionUser.OwnExtractFolder(context.Locations.ExtractDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.Runtime.InteropServices.COMException)
        {
            return new(Outcome.Failed, ex.Message);
        }
        return FlowResult.Ok;
    }

    public async Task<FlowResult> UninstallAsync(bool removeUserData, IProgress<InstallProgress>? progress)
    {
        // Machine half 2 steps + user half 4 steps. Files first: if the admin prompt is declined, nothing has changed.
        const int total = 6;
        progress?.Report(new(0, total, "Closing TurboDL"));
        if (!await RunningApp.QuitAsync(context.Locations.InstallDir, QuitTimeout)) return new(Outcome.AppRunning, CloseTurboDL);

        var error = await Worker.RunAsync(context, "uninstall", Offset(progress, 0, total));
        if (error is not null) return error == Worker.Declined ? new(Outcome.Declined, error) : new(Outcome.Failed, error);

        try
        {
            context.Engine.UninstallUser(removeUserData, Offset(progress, 2, total), SessionUser.OwnExtractFolder(context.Locations.ExtractDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(Outcome.Failed, ex.Message);
        }
        return FlowResult.Ok;
    }

    /// <summary>
    /// Starts the installed TurboDL as the user (Setup itself runs unelevated). A test install runs on a scratch
    /// download list so it never touches the real one.
    /// </summary>
    public void Launch()
    {
        var start = new ProcessStartInfo(context.Locations.AppPath)
        {
            UseShellExecute = false,
            WorkingDirectory = context.Locations.InstallDir,
        };
        if (context.IsTest) start.ArgumentList.Add($"--db={Path.Combine(context.Locations.UserDataDir, "turbodl.db")}");
        Process.Start(start)?.Dispose();
    }

    private static IProgress<InstallProgress>? Offset(IProgress<InstallProgress>? progress, int before, int total) =>
        progress is null ? null : new InlineProgress<InstallProgress>(p => progress.Report(new(before + p.Done, total, p.Text)));
}
