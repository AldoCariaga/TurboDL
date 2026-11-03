using System.Text.Json;
using System.Text.RegularExpressions;

namespace TurboDL.Installer;

/// <summary>
/// The native messaging registration: com.turbodl.host.json next to the host, and a per-user key for Chrome and for
/// Edge whose default value is that file's path. Only the TurboDL extension may connect.
/// </summary>
public static partial class BrowserHosts
{
    public static IReadOnlyList<(string Browser, string Key)> Keys { get; } =
    [
        ("Chrome", $@"Google\Chrome\NativeMessagingHosts\{Product.HostName}"),
        ("Edge", $@"Microsoft\Edge\NativeMessagingHosts\{Product.HostName}"),
    ];

    public static bool IsExtensionId(string id) => ExtensionIdPattern().IsMatch(id);

    /// <summary>The manifest's JSON. Chrome and Edge read the same format.</summary>
    public static string ManifestJson(string hostPath, string extensionId)
    {
        if (!IsExtensionId(extensionId)) throw new ArgumentException($"'{extensionId}' isn't an extension ID (32 letters a-p).", nameof(extensionId));
        return JsonSerializer.Serialize(new
        {
            name = Product.HostName,
            description = "TurboDL download manager",
            path = hostPath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void WriteManifest(InstallLocations locations, string extensionId) =>
        File.WriteAllText(locations.ManifestPath, ManifestJson(locations.HostPath, extensionId));

    /// <summary>Points both browsers at the manifest (replacing any earlier registration, e.g. a dev build's).</summary>
    public static void Register(RegistryLocations registry, string manifestPath)
    {
        foreach (var (_, key) in Keys)
        {
            using var k = registry.CreateUser(key);
            k.SetValue("", manifestPath);
        }
    }

    /// <summary>
    /// Deletes both keys, then their parents when they're left empty (Register may have created them on a PC without
    /// that browser). Never above the vendor key, and never Software\Microsoft itself.
    /// </summary>
    public static void Unregister(RegistryLocations registry)
    {
        foreach (var (_, key) in Keys)
        {
            registry.DeleteUser(key);
            for (var parent = ParentOf(key);
                 parent is not null && !string.Equals(parent, "Microsoft", StringComparison.OrdinalIgnoreCase);
                 parent = ParentOf(parent))
            {
                using (var k = registry.OpenUser(parent))
                {
                    if (k is null) continue;
                    if (k.SubKeyCount > 0 || k.ValueCount > 0) break;
                }
                registry.DeleteUser(parent);
            }
        }
    }

    private static string? ParentOf(string key) => key.LastIndexOf('\\') is var i and > 0 ? key[..i] : null;

    /// <summary>The manifest path a browser's key points at, or null when it isn't registered.</summary>
    public static string? RegisteredManifest(RegistryLocations registry, string key)
    {
        using var k = registry.OpenUser(key);
        return k?.GetValue("") as string;
    }

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ExtensionIdPattern();
}
