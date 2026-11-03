namespace TurboDL.Installer;

/// <summary>TurboDL's line in Windows' Installed apps: HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\TurboDL.</summary>
public static class UninstallEntry
{
    public const string Key = $@"Microsoft\Windows\CurrentVersion\Uninstall\{Product.Name}";

    public static void Write(RegistryLocations registry, InstallLocations locations, string version, long installedBytes, DateTime today)
    {
        using var key = registry.CreateMachine(Key);
        key.SetValue("DisplayName", Product.Name);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Product.Publisher);
        key.SetValue("DisplayIcon", $"\"{locations.AppPath}\",0");
        key.SetValue("InstallLocation", locations.InstallDir);
        key.SetValue("InstallDate", today.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, (installedBytes + 1023) / 1024), Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("UninstallString", $"\"{locations.SetupPath}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{locations.SetupPath}\" /uninstall /S");
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
    }

    public static void Delete(RegistryLocations registry) => registry.DeleteMachine(Key);

    /// <summary>The installed copy's folder and version, or null when TurboDL isn't installed.</summary>
    public static (string InstallDir, string Version)? Read(RegistryLocations registry)
    {
        using var key = registry.OpenMachine(Key);
        if (key?.GetValue("InstallLocation") is not string { Length: > 0 } dir) return null;
        return (dir, key.GetValue("DisplayVersion") as string ?? "");
    }
}
