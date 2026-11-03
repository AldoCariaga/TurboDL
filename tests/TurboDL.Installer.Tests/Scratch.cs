using Microsoft.Win32;
using TurboDL.Installer;

namespace TurboDL.Installer.Tests;

/// <summary>A scratch folder and a scratch HKCU key standing in for Program Files, the profile, HKCU and HKLM.</summary>
public sealed class Scratch : IDisposable
{
    public const string ExtensionId = "bmffkkpdabfjihjbmhmammbckmeaoblf";

    public string Root { get; }
    public string KeyName { get; } = "test-" + Guid.NewGuid().ToString("N");
    public InstallLocations Locations { get; }
    public RegistryLocations Registry { get; }

    public Scratch()
    {
        Root = Path.Combine(Path.GetTempPath(), "turbodl-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Locations = InstallLocations.ForTest(Root);
        Registry = RegistryLocations.ForTest(KeyName);
    }

    public Installer NewInstaller(InstallLocations? locations = null) => new(locations ?? Locations, Registry);

    /// <summary>Stand-ins for the three exes: a few bytes each, different per <paramref name="build"/>.</summary>
    public static IReadOnlyList<PayloadFile> Payload(string build = "1") =>
    [
        new(Product.AppExe, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("app " + build))),
        new(Product.HostExe, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("host " + build))),
        new(Product.SetupExe, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("setup " + build))),
    ];

    /// <summary>Every value under the scratch key, as "key path\name = data", empty keys as "key path\".</summary>
    public List<string> RegistryContents()
    {
        var found = new List<string>();
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{RegistryLocations.TestKeyParent}\{KeyName}");
        if (root is not null) Walk(root, "", found);
        return found;
    }

    private static void Walk(RegistryKey key, string path, List<string> found)
    {
        foreach (var name in key.GetValueNames()) found.Add($@"{path}\{name} = {key.GetValue(name)}");
        if (key.ValueCount == 0 && key.SubKeyCount == 0) found.Add(path + @"\");
        foreach (var sub in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(sub)!;
            Walk(child, $@"{path}\{sub}", found);
        }
    }

    /// <summary>Every file under the scratch folder, relative to it.</summary>
    public List<string> Files() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f)).ToList();

    public void Dispose()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{RegistryLocations.TestKeyParent}\{KeyName}", throwOnMissingSubKey: false);
        try
        {
            // Throws when another test's key is still there; then that test removes the parent.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(RegistryLocations.TestKeyParent, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException) { }
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
