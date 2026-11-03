using System.Text.Json;
using TurboDL.Installer;

namespace TurboDL.Installer.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly Scratch _s = new();

    public void Dispose() => _s.Dispose();

    private void InstallAll(Installer installer, InstallOptions options, string build = "1", string version = "0.6.0")
    {
        installer.InstallMachine(Scratch.Payload(build), Scratch.ExtensionId, version);
        installer.InstallUser(options);
    }

    [Fact]
    public void Install_puts_everything_in_place()
    {
        var installer = _s.NewInstaller();
        var loc = _s.Locations;
        var steps = new List<InstallProgress>();

        installer.InstallMachine(Scratch.Payload(), Scratch.ExtensionId, "0.6.0", new SyncProgress(steps));
        installer.InstallUser(new InstallOptions(StartMenuShortcut: true, DesktopShortcut: true, StartWithWindows: true), new SyncProgress(steps));

        Assert.Equal("app 1", File.ReadAllText(loc.AppPath));
        Assert.Equal("host 1", File.ReadAllText(loc.HostPath));
        Assert.Equal("setup 1", File.ReadAllText(loc.SetupPath));
        Assert.DoesNotContain(Directory.GetFiles(loc.InstallDir), f => f.EndsWith(".new"));

        using (var manifest = JsonDocument.Parse(File.ReadAllText(loc.ManifestPath)))
        {
            var m = manifest.RootElement;
            Assert.Equal("com.turbodl.host", m.GetProperty("name").GetString());
            Assert.Equal(loc.HostPath, m.GetProperty("path").GetString());
            Assert.Equal("stdio", m.GetProperty("type").GetString());
            Assert.Equal($"chrome-extension://{Scratch.ExtensionId}/", Assert.Single(m.GetProperty("allowed_origins").EnumerateArray()).GetString());
        }

        foreach (var (_, key) in BrowserHosts.Keys) Assert.Equal(loc.ManifestPath, BrowserHosts.RegisteredManifest(_s.Registry, key));
        Assert.True(Shortcut.Targets(loc.StartMenuShortcut, loc.AppPath));
        Assert.True(Shortcut.Targets(loc.DesktopShortcut, loc.AppPath));
        Assert.True(StartupEntry.IsOurs(_s.Registry, loc.AppPath));
        Assert.Contains($@"\HKCU\Microsoft\Windows\CurrentVersion\Run\TurboDL = ""{loc.AppPath}"" --tray", _s.RegistryContents());

        Assert.Equal((loc.InstallDir, "0.6.0"), installer.Existing());
        var entry = _s.RegistryContents().Where(v => v.Contains(@"\Uninstall\TurboDL\")).ToList();
        Assert.Contains(@$"\HKLM\Microsoft\Windows\CurrentVersion\Uninstall\TurboDL\UninstallString = ""{loc.SetupPath}"" /uninstall", entry);
        Assert.Contains(@$"\HKLM\Microsoft\Windows\CurrentVersion\Uninstall\TurboDL\QuietUninstallString = ""{loc.SetupPath}"" /uninstall /S", entry);
        Assert.Contains(@"\HKLM\Microsoft\Windows\CurrentVersion\Uninstall\TurboDL\DisplayName = TurboDL", entry);
        Assert.Contains(@"\HKLM\Microsoft\Windows\CurrentVersion\Uninstall\TurboDL\EstimatedSize = 1", entry);

        Assert.Equal(new InstallProgress(3, 3, "Files installed"), steps[3]);
        Assert.Equal(new InstallProgress(4, 4, "Done"), steps[^1]);
    }

    [Fact]
    public void First_install_offers_the_defaults_and_an_upgrade_keeps_the_choices()
    {
        var installer = _s.NewInstaller();
        Assert.Null(installer.Existing());
        Assert.Equal(InstallOptions.Defaults, installer.CurrentOptions());
        Assert.Equal(new InstallOptions(true, false, false), InstallOptions.Defaults);

        var chosen = new InstallOptions(StartMenuShortcut: false, DesktopShortcut: true, StartWithWindows: true);
        InstallAll(installer, chosen);
        Assert.Equal(chosen, _s.NewInstaller().CurrentOptions());

        // The upgrade replaces the files; the user's choices stay what they were.
        InstallAll(_s.NewInstaller(), _s.NewInstaller().CurrentOptions(), build: "2", version: "0.7.0");
        Assert.Equal("app 2", File.ReadAllText(_s.Locations.AppPath));
        Assert.Equal(chosen, _s.NewInstaller().CurrentOptions());
        Assert.Equal((_s.Locations.InstallDir, "0.7.0"), _s.NewInstaller().Existing());
        Assert.False(File.Exists(_s.Locations.StartMenuShortcut));
    }

    [Fact]
    public void Reinstalling_with_options_off_removes_what_they_added()
    {
        InstallAll(_s.NewInstaller(), new InstallOptions(true, true, true));
        InstallAll(_s.NewInstaller(), new InstallOptions(false, false, false));

        Assert.False(File.Exists(_s.Locations.StartMenuShortcut));
        Assert.False(File.Exists(_s.Locations.DesktopShortcut));
        Assert.False(StartupEntry.IsOurs(_s.Registry, _s.Locations.AppPath));
    }

    [Fact]
    public void Install_then_uninstall_leaves_nothing_behind()
    {
        InstallAll(_s.NewInstaller(), new InstallOptions(true, true, true));
        InstallAll(_s.NewInstaller(), _s.NewInstaller().CurrentOptions(), build: "2");
        Directory.CreateDirectory(Path.Combine(_s.Locations.ExtractDir, "TurboDL.App", "abc"));
        File.WriteAllText(Path.Combine(_s.Locations.ExtractDir, "TurboDL.App", "abc", "e_sqlite3.dll"), "x");

        var installer = _s.NewInstaller();
        installer.UninstallUser(removeUserData: false);
        Assert.True(installer.UninstallMachine());

        Assert.Empty(_s.Files());
        Assert.False(Directory.Exists(_s.Locations.InstallDir));
        // Only the always-present Run key is left (empty here, since the scratch hive starts empty).
        Assert.Equal([@"\HKCU\Microsoft\Windows\CurrentVersion\Run\", @"\HKLM\Microsoft\Windows\CurrentVersion\Uninstall\"],
            _s.RegistryContents().Order());
        Assert.Null(_s.NewInstaller().Existing());

        // Uninstalling again is harmless.
        installer.UninstallUser(removeUserData: false);
        Assert.True(installer.UninstallMachine());
    }

    [Fact]
    public void Uninstall_leaves_other_peoples_things_alone()
    {
        InstallAll(_s.NewInstaller(), new InstallOptions(false, false, false));
        var loc = _s.Locations;
        var otherExe = Path.Combine(_s.Root, "other", "Other.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!);
        File.WriteAllText(otherExe, "");
        Shortcut.Create(loc.StartMenuShortcut, otherExe, _s.Root, "someone else's");
        using (var run = _s.Registry.CreateUser(StartupEntry.RunKey)) run.SetValue("TurboDL", $"\"{otherExe}\" --tray");
        File.WriteAllText(Path.Combine(loc.InstallDir, "notes.txt"), "mine");
        using (var vendor = _s.Registry.CreateUser(@"Google\Chrome")) vendor.SetValue("x", 1);

        var installer = _s.NewInstaller();
        installer.UninstallUser(removeUserData: false);
        Assert.False(installer.UninstallMachine());

        Assert.True(File.Exists(loc.StartMenuShortcut));
        Assert.Contains($@"\HKCU\Microsoft\Windows\CurrentVersion\Run\TurboDL = ""{otherExe}"" --tray", _s.RegistryContents());
        Assert.Equal(["notes.txt"], Directory.GetFiles(loc.InstallDir).Select(Path.GetFileName));
        Assert.Contains(@"\HKCU\Google\Chrome\x = 1", _s.RegistryContents());
        Assert.DoesNotContain(_s.RegistryContents(), v => v.Contains("NativeMessagingHosts"));
        Assert.DoesNotContain(_s.RegistryContents(), v => v.Contains(@"\Edge"));
    }

    [Fact]
    public void Installing_into_a_new_folder_removes_the_old_copy()
    {
        InstallAll(_s.NewInstaller(), InstallOptions.Defaults);
        var oldDir = _s.Locations.InstallDir;
        var moved = InstallLocations.ForTest(_s.Root, Path.Combine(_s.Root, "Elsewhere", "TurboDL"));

        InstallAll(_s.NewInstaller(moved), InstallOptions.Defaults);

        Assert.False(Directory.Exists(oldDir));
        Assert.True(File.Exists(moved.AppPath));
        Assert.Equal((moved.InstallDir, "0.6.0"), _s.NewInstaller(moved).Existing());
        Assert.True(Shortcut.Targets(moved.StartMenuShortcut, moved.AppPath));
        foreach (var (_, key) in BrowserHosts.Keys) Assert.Equal(moved.ManifestPath, BrowserHosts.RegisteredManifest(_s.Registry, key));
    }

    [Fact]
    public void Uninstall_keeps_the_download_list_unless_asked_and_always_drops_the_tools()
    {
        var loc = _s.Locations;
        void Seed()
        {
            Directory.CreateDirectory(Cleanup.ToolsDir(loc));
            File.WriteAllText(Path.Combine(Cleanup.ToolsDir(loc), "yt-dlp.exe"), "x");
            File.WriteAllText(Path.Combine(loc.UserDataDir, "turbodl.db"), "list");
            File.WriteAllText(Path.Combine(loc.UserDataDir, "settings.json"), "{}");
        }

        Seed();
        _s.NewInstaller().UninstallUser(removeUserData: false);
        Assert.False(Directory.Exists(Cleanup.ToolsDir(loc)));
        Assert.True(File.Exists(Path.Combine(loc.UserDataDir, "turbodl.db")));
        Assert.True(File.Exists(Path.Combine(loc.UserDataDir, "settings.json")));

        Seed();
        _s.NewInstaller().UninstallUser(removeUserData: true);
        Assert.False(Directory.Exists(loc.UserDataDir));
    }

    [Fact]
    public void A_bad_extension_id_is_refused_before_anything_is_written()
    {
        Assert.Throws<ArgumentException>(() => _s.NewInstaller().InstallMachine(Scratch.Payload(), "not-an-id", "0.6.0"));
        Assert.False(Directory.Exists(_s.Locations.InstallDir));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the reports in order, at once.</summary>
    private sealed class SyncProgress(List<InstallProgress> into) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => into.Add(value);
    }
}
