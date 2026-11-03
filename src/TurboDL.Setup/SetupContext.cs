using System.Reflection;
using TurboDL.Installer;
using Engine = TurboDL.Installer.Installer;

namespace TurboDL.Setup;

/// <summary>Where this run installs to, what it installs, and the version it carries.</summary>
public sealed class SetupContext
{
    private SetupContext(SetupArgs args, InstallLocations locations, RegistryLocations registry)
    {
        Args = args;
        Locations = locations;
        Registry = registry;
        Engine = new Engine(locations, registry);
    }

    public SetupArgs Args { get; }
    public InstallLocations Locations { get; }
    public RegistryLocations Registry { get; }
    public Engine Engine { get; }
    public bool IsTest => Args.TestRoot is not null;

    /// <summary>
    /// The folder: /dir, else the installed copy's folder (an upgrade stays where it is), else Program Files.
    /// With /testroot, everything maps into that scratch folder and a scratch registry key.
    /// </summary>
    public static SetupContext Create(SetupArgs args, string? installDir = null)
    {
        var registry = args.TestRoot is { } root
            ? RegistryLocations.ForTest(Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\')))
            : RegistryLocations.Real;
        var dir = installDir ?? args.InstallDir ?? UninstallEntry.Read(registry)?.InstallDir;
        var locations = args.TestRoot is { } testRoot
            ? InstallLocations.ForTest(testRoot, dir)
            : InstallLocations.ForCurrentUser(dir ?? InstallLocations.DefaultInstallDir);
        return new SetupContext(args, locations, registry);
    }

    public SetupContext WithInstallDir(string dir) => Create(Args with { InstallDir = dir });

    /// <summary>0.6.0: the version shown and written to Installed apps.</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>0.6.0+7af5209: with the commit, for the window's footer.</summary>
    public static string FullVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version;

    public static string ExtensionId { get; } = ReadResource("ExtensionId")?.Trim() ?? "";

    /// <summary>True when this build carries TurboDL's exes (scripts\publish.ps1 builds it that way).</summary>
    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload/" + Product.AppExe) is not null;

    /// <summary>The exes to install, plus this Setup itself (kept for uninstalling).</summary>
    public static IReadOnlyList<PayloadFile> Payload()
    {
        if (!HasPayload) throw new InvalidOperationException("This Setup was built without TurboDL's files. Build it with scripts\\publish.ps1.");
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Setup is running from.");
        return
        [
            new(Product.AppExe, () => Resource("payload/" + Product.AppExe)),
            new(Product.HostExe, () => Resource("payload/" + Product.HostExe)),
            new(Product.SetupExe, () => new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)),
        ];
    }

    private static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is missing from Setup.");

    private static string? ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
