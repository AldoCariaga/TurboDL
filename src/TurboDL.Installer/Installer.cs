namespace TurboDL.Installer;

/// <summary>The choices on Setup's Options page.</summary>
public sealed record InstallOptions(bool StartMenuShortcut, bool DesktopShortcut, bool StartWithWindows)
{
    /// <summary>A first install's defaults.</summary>
    public static InstallOptions Defaults { get; } = new(StartMenuShortcut: true, DesktopShortcut: false, StartWithWindows: false);
}

/// <summary>One step done: <see cref="Done"/> of <see cref="Total"/>, and what happens next.</summary>
public sealed record InstallProgress(int Done, int Total, string Text);

/// <summary>
/// Installs and uninstalls TurboDL. Split in two halves: the machine half (the install folder and the Installed apps
/// entry) needs admin rights; the user half (browser registration, shortcuts, startup, the user's temp and data
/// folders) must run as the user who's installing, so it lands in their registry and profile.
/// </summary>
public sealed class Installer(InstallLocations locations, RegistryLocations registry)
{
    public InstallLocations Locations => locations;

    /// <summary>The installed copy (from the Installed apps entry), or null on a first install.</summary>
    public (string InstallDir, string Version)? Existing() => UninstallEntry.Read(registry);

    /// <summary>
    /// What the Options page starts with: on an upgrade, what the current install has (so its choices are kept);
    /// otherwise the defaults.
    /// </summary>
    public InstallOptions CurrentOptions()
    {
        if (Existing() is null) return InstallOptions.Defaults;
        return new InstallOptions(
            StartMenuShortcut: Shortcut.Targets(locations.StartMenuShortcut, locations.AppPath),
            DesktopShortcut: Shortcut.Targets(locations.DesktopShortcut, locations.AppPath),
            StartWithWindows: StartupEntry.IsOurs(registry, locations.AppPath));
    }

    /// <summary>Copies the files, writes the host manifest and the Installed apps entry. Needs admin for Program Files.</summary>
    public void InstallMachine(IReadOnlyList<PayloadFile> payload, string extensionId, string version, IProgress<InstallProgress>? progress = null)
    {
        const int total = 3;
        if (!BrowserHosts.IsExtensionId(extensionId)) throw new ArgumentException($"'{extensionId}' isn't an extension ID.", nameof(extensionId));

        // Moving to another folder: the old copy goes, or it would linger with no way to uninstall it.
        if (Existing() is { InstallDir: var oldDir } && !SamePath(oldDir, locations.InstallDir))
        {
            progress?.Report(new(0, total, $"Removing the old copy in {oldDir}"));
            InstallFiles.Delete(oldDir);
        }

        progress?.Report(new(0, total, "Copying files"));
        InstallFiles.Write(locations.InstallDir, payload);
        var legacy = Path.Combine(locations.InstallDir, Product.LegacyHostSettings);
        if (File.Exists(legacy)) File.Delete(legacy);

        progress?.Report(new(1, total, "Writing the browser host manifest"));
        BrowserHosts.WriteManifest(locations, extensionId);

        progress?.Report(new(2, total, "Adding TurboDL to Installed apps"));
        UninstallEntry.Write(registry, locations, version, InstallFiles.Size(locations.InstallDir), DateTime.Today);
        progress?.Report(new(total, total, "Files installed"));
    }

    /// <summary>Registers the host with Chrome and Edge, sets shortcuts and startup, cleans old unpack folders. Run as the user.</summary>
    public (int Deleted, int Kept) InstallUser(InstallOptions options, IProgress<InstallProgress>? progress = null, string? keepExtractFolder = null)
    {
        const int total = 4;
        progress?.Report(new(0, total, "Registering with Chrome and Edge"));
        BrowserHosts.Register(registry, locations.ManifestPath);

        progress?.Report(new(1, total, "Creating shortcuts"));
        SetShortcut(locations.StartMenuShortcut, options.StartMenuShortcut);
        SetShortcut(locations.DesktopShortcut, options.DesktopShortcut);

        progress?.Report(new(2, total, "Setting up startup"));
        StartupEntry.Set(registry, locations.AppPath, options.StartWithWindows);

        progress?.Report(new(3, total, "Cleaning up temporary files"));
        var cleaned = Cleanup.ExtractFolders(locations.ExtractDir, keepExtractFolder);
        progress?.Report(new(total, total, "Done"));
        return cleaned;
    }

    /// <summary>
    /// Removes the user's side: browser keys, our shortcuts, our Run value, unpack folders, the downloaded tools and,
    /// with <paramref name="removeUserData"/>, the download list and settings. Run as the user.
    /// </summary>
    public (int Deleted, int Kept) UninstallUser(bool removeUserData, IProgress<InstallProgress>? progress = null, string? keepExtractFolder = null)
    {
        const int total = 4;
        progress?.Report(new(0, total, "Unregistering from Chrome and Edge"));
        BrowserHosts.Unregister(registry);

        progress?.Report(new(1, total, "Removing shortcuts and startup"));
        Shortcut.DeleteIfTargets(locations.StartMenuShortcut, locations.AppPath);
        Shortcut.DeleteIfTargets(locations.DesktopShortcut, locations.AppPath);
        StartupEntry.Set(registry, locations.AppPath, enabled: false);

        progress?.Report(new(2, total, "Cleaning up temporary files"));
        var cleaned = Cleanup.ExtractFolders(locations.ExtractDir, keepExtractFolder);

        progress?.Report(new(3, total, removeUserData ? "Removing your download list and settings" : "Removing downloaded tools"));
        Cleanup.UserData(locations, tools: true, userData: removeUserData);
        progress?.Report(new(total, total, "Done"));
        return cleaned;
    }

    /// <summary>Deletes the files, the folder (when nothing else is in it) and the Installed apps entry. Needs admin.</summary>
    public bool UninstallMachine(IProgress<InstallProgress>? progress = null)
    {
        const int total = 2;
        progress?.Report(new(0, total, "Removing files"));
        var folderGone = InstallFiles.Delete(locations.InstallDir);

        progress?.Report(new(1, total, "Removing TurboDL from Installed apps"));
        UninstallEntry.Delete(registry);
        progress?.Report(new(total, total, "Done"));
        return folderGone;
    }

    private void SetShortcut(string path, bool wanted)
    {
        if (wanted) Shortcut.Create(path, locations.AppPath, locations.InstallDir, "TurboDL download manager");
        else Shortcut.DeleteIfTargets(path, locations.AppPath);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
