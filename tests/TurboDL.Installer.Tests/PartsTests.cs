using TurboDL.Installer;

namespace TurboDL.Installer.Tests;

public sealed class PartsTests : IDisposable
{
    private readonly Scratch _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void Unpack_folders_are_deleted_except_ones_in_use_or_excepted()
    {
        var extract = _s.Locations.ExtractDir;
        string Make(string app, string id)
        {
            var dir = Path.Combine(extract, app, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "wpfgfx_cor3.dll"), "x");
            return dir;
        }

        Make("TurboDL.App", "old1");
        var inUse = Make("TurboDL.App", "running");
        Make("TurboDL.NativeHost", "old2");
        var self = Make("TurboDL.Setup", "self");
        var foreign = Make("SomeOtherApp", "keep");
        Make("TurboDL-Setup-0.6.0", "downloaded");
        Make("TurboDL-Setup-0.6.0 (1)", "again");

        var loaded = Make("TurboDL.NativeHost", "loaded");
        bool InUse(string app, string folder) => folder == loaded;

        (int, int) result;
        using (File.Open(Path.Combine(inUse, "wpfgfx_cor3.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            result = Cleanup.ExtractFolders(extract, except: self, inUse: InUse);

        Assert.Equal((4, 3), result);
        Assert.False(Directory.Exists(Path.Combine(extract, "TurboDL-Setup-0.6.0")));
        Assert.False(Directory.Exists(Path.Combine(extract, "TurboDL-Setup-0.6.0 (1)")));
        Assert.Equal(["running"], Directory.GetDirectories(Path.Combine(extract, "TurboDL.App")).Select(Path.GetFileName));
        Assert.Equal(["loaded"], Directory.GetDirectories(Path.Combine(extract, "TurboDL.NativeHost")).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(loaded, "wpfgfx_cor3.dll")));
        Assert.True(Directory.Exists(self));
        Assert.True(Directory.Exists(foreign));

        Assert.Equal((2, 1), Cleanup.ExtractFolders(extract, except: self, inUse: (_, _) => false));
        Assert.False(Directory.Exists(Path.Combine(extract, "TurboDL.NativeHost")));
        Assert.Equal((0, 0), Cleanup.ExtractFolders(Path.Combine(_s.Root, "nowhere")));
    }

    [Fact]
    public void A_folder_this_process_loaded_a_library_from_counts_as_in_use()
    {
        var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Assert.True(Cleanup.UsedByRunningProcess(name, runtimeDir));
        Assert.False(Cleanup.UsedByRunningProcess(name, _s.Root));
        Assert.False(Cleanup.UsedByRunningProcess("TurboDL.NoSuchApp", runtimeDir));
    }

    [Fact]
    public void Startup_entry_matches_the_apps_own_format_and_only_removes_its_own()
    {
        var app = @"C:\Program Files\TurboDL\TurboDL.App.exe";
        Assert.Equal("\"C:\\Program Files\\TurboDL\\TurboDL.App.exe\" --tray", StartupEntry.Command(app));

        StartupEntry.Set(_s.Registry, app, enabled: true);
        Assert.True(StartupEntry.IsOurs(_s.Registry, app.ToUpperInvariant()));
        Assert.False(StartupEntry.IsOurs(_s.Registry, @"D:\TurboDL\TurboDL.App.exe"));

        StartupEntry.Set(_s.Registry, @"D:\TurboDL\TurboDL.App.exe", enabled: false);
        Assert.True(StartupEntry.IsOurs(_s.Registry, app));

        StartupEntry.Set(_s.Registry, app, enabled: false);
        Assert.False(StartupEntry.IsOurs(_s.Registry, app));
        StartupEntry.Set(_s.Registry, app, enabled: false);
    }

    [Fact]
    public void Shortcuts_round_trip_and_only_ours_are_deleted()
    {
        var target = Path.Combine(_s.Root, "App", "TurboDL.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "");
        var lnk = Path.Combine(_s.Root, "Start Menu", "TurboDL.lnk");

        Assert.Null(Shortcut.ReadTarget(lnk));
        Shortcut.Create(lnk, target, Path.GetDirectoryName(target)!, "TurboDL download manager");
        Assert.Equal(target, Shortcut.ReadTarget(lnk), ignoreCase: true);

        Assert.False(Shortcut.DeleteIfTargets(lnk, Path.Combine(_s.Root, "Other.exe")));
        Assert.True(File.Exists(lnk));
        Assert.True(Shortcut.DeleteIfTargets(lnk, target));
        Assert.False(File.Exists(lnk));

        File.WriteAllText(lnk, "not a shortcut");
        Assert.Null(Shortcut.ReadTarget(lnk));
    }

    [Fact]
    public void A_file_in_use_is_retried_then_reported()
    {
        var dir = _s.Locations.InstallDir;
        InstallFiles.Write(dir, Scratch.Payload());
        var old = InstallFiles.InUseTimeout;
        InstallFiles.InUseTimeout = TimeSpan.FromMilliseconds(600);
        try
        {
            using (File.Open(Path.Combine(dir, Product.HostExe), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() => InstallFiles.Write(dir, Scratch.Payload("2")));
                Assert.Contains(Product.HostExe, ex.Message);
            }

            // Released within the timeout: it goes through.
            var holder = File.Open(Path.Combine(dir, Product.HostExe), FileMode.Open, FileAccess.Read, FileShare.None);
            _ = Task.Delay(200).ContinueWith(_ => holder.Dispose());
            InstallFiles.Write(dir, Scratch.Payload("3"));
            Assert.Equal("host 3", File.ReadAllText(Path.Combine(dir, Product.HostExe)));
            Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".new"));
        }
        finally
        {
            InstallFiles.InUseTimeout = old;
        }
    }

    [Fact]
    public void Extension_ids_are_checked()
    {
        Assert.True(BrowserHosts.IsExtensionId(Scratch.ExtensionId));
        Assert.False(BrowserHosts.IsExtensionId("bmffkkpdabfjihjbmhmammbckmeaobl"));
        Assert.False(BrowserHosts.IsExtensionId("zmffkkpdabfjihjbmhmammbckmeaoblf"));
        Assert.Throws<ArgumentException>(() => BrowserHosts.ManifestJson(@"C:\x.exe", "nope"));
    }

    [Fact]
    public void Test_registry_names_stay_under_the_test_key()
    {
        Assert.Throws<ArgumentException>(() => RegistryLocations.ForTest(@"..\Software"));
        Assert.Throws<ArgumentException>(() => RegistryLocations.ForTest(""));
        Assert.StartsWith(@"HKEY_CURRENT_USER\Software\TurboDL.SetupTest\", RegistryLocations.ForTest("x").DescribeMachine("Uninstall"));
    }
}
