using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using TurboDL.Core.Sites;

namespace TurboDL.App.Services;

/// <summary>
/// Holds the site configs in use: the built-in ones, and the user's JSON files in <see cref="Folder"/>. They're read at
/// start and again on <see cref="Reload"/> (Settings), never in between, so a download sees one set from start to end.
/// </summary>
public sealed class SiteConfigService
{
    private readonly ILogger<SiteConfigService> _log;
    private SiteCatalog _catalog = SiteCatalog.Empty;

    /// <param name="folder">The user's sites folder; null is %LOCALAPPDATA%\TurboDL\sites.</param>
    public SiteConfigService(string? folder, ILogger<SiteConfigService> log)
    {
        _log = log;
        Folder = folder ?? DefaultFolder;
        Reload();
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL", "sites");

    /// <summary>Where the user's own site configs go (*.json). It may not exist yet.</summary>
    public string Folder { get; }

    public SiteCatalog Catalog => Volatile.Read(ref _catalog);

    /// <summary>Raised after <see cref="Reload"/>.</summary>
    public event Action? Changed;

    /// <summary>Reads the configs again; downloads started from now on use the new ones.</summary>
    public void Reload()
    {
        var catalog = SiteConfigLoader.Load(Folder);
        foreach (var problem in catalog.Problems)
            _log.LogWarning("Site config {File} not loaded: {Reason}", problem.File, problem.Reason);
        Volatile.Write(ref _catalog, catalog);
        Changed?.Invoke();
    }

    /// <summary>Opens the sites folder in Explorer, making it first if it's missing.</summary>
    public void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
    }
}
