using System.Diagnostics;
using System.Text.Json;

namespace TurboDL.NativeHost;

/// <summary>
/// Starts TurboDL.App.exe with <c>--tray</c>. Where it is, first match wins:
/// the TURBODL_APP_PATH environment variable; <c>appPath</c> in host-settings.json next to the host
/// (scripts\install.ps1 writes it when the two aren't side by side, as in a dev build); TurboDL.App.exe next to the host.
/// </summary>
public sealed class AppLauncher(string appPath, HostLog log) : IAppLauncher
{
    public const string AppExeName = "TurboDL.App.exe";
    public const string SettingsFileName = "host-settings.json";

    public string AppPath { get; } = appPath;

    public static string ResolveAppPath(string hostDirectory)
    {
        if (Environment.GetEnvironmentVariable("TURBODL_APP_PATH") is { Length: > 0 } fromEnv) return fromEnv;

        var settings = Path.Combine(hostDirectory, SettingsFileName);
        if (File.Exists(settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("appPath", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } fromFile)
                    return Path.GetFullPath(fromFile, hostDirectory);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fall through to the default; Launch reports a missing exe clearly.
            }
        }

        return Path.Combine(hostDirectory, AppExeName);
    }

    public void Launch()
    {
        if (!File.Exists(AppPath)) throw new FileNotFoundException($"{AppPath} doesn't exist.", AppPath);

        // Shell-execute so the app inherits none of our handles: our stdin/stdout are Chrome's pipes, and an app
        // holding them would keep Chrome's port from closing.
        using var process = Process.Start(new ProcessStartInfo(AppPath, "--tray")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(AppPath)!,
        }) ?? throw new InvalidOperationException($"{AppPath} didn't start.");
        log.Write($"Started {AppPath} (pid {process.Id}).");
    }
}
