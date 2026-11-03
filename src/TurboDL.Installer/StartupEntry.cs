namespace TurboDL.Installer;

/// <summary>
/// "Start TurboDL with Windows": the value TurboDL under HKCU\...\Run. Same format as the app's own setting
/// (<c>"&lt;exe&gt;" --tray</c>), so either one can turn it off.
/// </summary>
public static class StartupEntry
{
    public const string RunKey = @"Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = Product.Name;

    public static string Command(string appPath) => $"\"{appPath}\" --tray";

    /// <summary>True when the Run value starts this <paramref name="appPath"/>.</summary>
    public static bool IsOurs(RegistryLocations registry, string appPath)
    {
        using var key = registry.OpenUser(RunKey);
        return key?.GetValue(ValueName) is string command
               && command.Contains($"\"{appPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>On: write the value. Off: remove it, but only if it's ours (another TurboDL copy's entry stays).</summary>
    public static void Set(RegistryLocations registry, string appPath, bool enabled)
    {
        if (enabled)
        {
            using var key = registry.CreateUser(RunKey);
            key.SetValue(ValueName, Command(appPath));
        }
        else if (IsOurs(registry, appPath))
        {
            using var key = registry.OpenUser(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
