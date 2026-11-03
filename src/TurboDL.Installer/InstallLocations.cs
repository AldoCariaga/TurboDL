namespace TurboDL.Installer;

/// <summary>The fixed names of what TurboDL installs.</summary>
public static class Product
{
    public const string Name = "TurboDL";
    public const string Publisher = "TurboDL";
    public const string AppExe = "TurboDL.App.exe";
    public const string HostExe = "TurboDL.NativeHost.exe";

    /// <summary>The copy of Setup kept in the install folder; Installed apps runs it to uninstall.</summary>
    public const string SetupExe = "TurboDL.Setup.exe";

    public const string HostName = "com.turbodl.host";
    public const string ManifestFile = HostName + ".json";

    /// <summary>Written next to dev builds by the old install.ps1; never part of a real install.</summary>
    public const string LegacyHostSettings = "host-settings.json";

    public const string ShortcutFile = "TurboDL.lnk";

    /// <summary>Every file Setup puts in the install folder.</summary>
    public static IReadOnlyList<string> InstalledFiles { get; } = [AppExe, HostExe, SetupExe, ManifestFile];
}

/// <summary>
/// Where things go on disk. <see cref="ForCurrentUser"/> gives the real places; tests point everything at a scratch
/// folder. The folders other than <see cref="InstallDir"/> belong to the user, so build this in the user's process.
/// </summary>
public sealed record InstallLocations(
    string InstallDir,
    string StartMenuDir,
    string DesktopDir,
    string ExtractDir,
    string UserDataDir)
{
    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product.Name);

    public string AppPath => Path.Combine(InstallDir, Product.AppExe);
    public string HostPath => Path.Combine(InstallDir, Product.HostExe);
    public string SetupPath => Path.Combine(InstallDir, Product.SetupExe);
    public string ManifestPath => Path.Combine(InstallDir, Product.ManifestFile);
    public string StartMenuShortcut => Path.Combine(StartMenuDir, Product.ShortcutFile);
    public string DesktopShortcut => Path.Combine(DesktopDir, Product.ShortcutFile);

    /// <summary>The current user's Start menu, desktop, single-file unpack folder and TurboDL data folder.</summary>
    public static InstallLocations ForCurrentUser(string installDir) => new(
        Path.GetFullPath(installDir),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        DefaultExtractDir,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name));

    /// <summary>Everything inside <paramref name="testRoot"/>: Program Files, Start menu, desktop, temp and data.</summary>
    public static InstallLocations ForTest(string testRoot, string? installDir = null)
    {
        var root = Path.GetFullPath(testRoot);
        return new(
            Path.GetFullPath(installDir ?? Path.Combine(root, "Program Files", Product.Name)),
            Path.Combine(root, "Start Menu", "Programs"),
            Path.Combine(root, "Desktop"),
            Path.Combine(root, "Temp", ".net"),
            Path.Combine(root, "LocalAppData", Product.Name));
    }

    /// <summary>Where .NET unpacks single-file apps' native libraries: DOTNET_BUNDLE_EXTRACT_BASE_DIR, else %TEMP%\.net.</summary>
    public static string DefaultExtractDir =>
        Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Path.GetTempPath(), ".net");
}
