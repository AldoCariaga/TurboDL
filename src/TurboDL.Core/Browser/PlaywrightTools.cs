using System.Formats.Tar;
using System.IO.Compression;
using TurboDL.Core.Engine;
using TurboDL.Core.Video;

namespace TurboDL.Core.Browser;

/// <summary>
/// Where the browser helper comes from. Playwright's driver is two public, versioned files: the playwright-core package
/// from npm and Node's node.exe. Both are pinned and checked against their SHA-256. They must match the Microsoft.Playwright
/// NuGet package the App uses (its bundled driver is playwright-core <see cref="DriverVersion"/> on Node <see cref="NodeVersion"/>).
/// </summary>
public sealed class PlaywrightSources
{
    public string DriverVersion { get; init; } = "1.64.1";
    public string DriverUrl { get; init; } = "https://registry.npmjs.org/playwright-core/-/playwright-core-1.64.1.tgz";
    public string DriverSha256 { get; init; } = "3af03703c967c95037454f2283f709d53ce804a4776d4219d319f5bbcc2a0185";

    public string NodeVersion { get; init; } = "24.22.1";
    public string NodeUrl { get; init; } = "https://nodejs.org/dist/v24.22.1/win-x64/node.exe";
    public string NodeSha256 { get; init; } = "d702daa815eabbff763c7d3808e235cf031cf2ca70211b56847bc72f51e240e5";
}

/// <summary>
/// Playwright's driver, downloaded on first use into &lt;tools&gt;\playwright in the layout Playwright looks for
/// (<c>.playwright\node\win32_x64\node.exe</c> and <c>.playwright\package</c>), so the single-file app ships none of it.
/// Point PLAYWRIGHT_DRIVER_SEARCH_PATH at <see cref="Directory"/>. No browser is downloaded: the installed Chrome, or else Edge, is used.
/// </summary>
public sealed class PlaywrightTools
{
    /// <summary>How much setup downloads, for the user-facing message.</summary>
    public const string SetupSizeText = "about 95 MB";

    private const string ToolName = "Browser helper";

    private readonly HttpClient _http;
    private readonly PlaywrightSources _sources;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlaywrightTools(string? toolsDirectory = null, HttpClient? http = null, PlaywrightSources? sources = null)
    {
        Directory = Path.Combine(toolsDirectory ?? YtDlpTools.DefaultDirectory, "playwright");
        _http = http ?? TurboHttp.CreateClient();
        _sources = sources ?? new PlaywrightSources();
    }

    /// <summary>&lt;tools&gt;\playwright: the value for PLAYWRIGHT_DRIVER_SEARCH_PATH.</summary>
    public string Directory { get; }

    private string DriverRoot => Path.Combine(Directory, ".playwright");
    public string NodePath => Path.Combine(DriverRoot, "node", "win32_x64", "node.exe");
    public string PackageDirectory => Path.Combine(DriverRoot, "package");
    private string VersionFile => Path.Combine(Directory, "version.txt");
    private string VersionText => $"playwright-core {_sources.DriverVersion}, node {_sources.NodeVersion}";

    /// <summary>True when this exact driver and Node are in place. A different version means set up again.</summary>
    public bool IsInstalled =>
        File.Exists(NodePath) && File.Exists(Path.Combine(PackageDirectory, "cli.js"))
        && File.Exists(VersionFile) && File.ReadAllText(VersionFile) == VersionText;

    /// <summary>A download that receives nothing for this long is abandoned (and can be resumed by calling again).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Installs the driver if it's missing or another version. Both downloads are checked against their pinned SHA-256
    /// before anything is unpacked or run. An interrupted download leaves a <c>.part</c> file that the next call resumes.
    /// </summary>
    public async Task EnsureAsync(IProgress<ToolSetupProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled) return;
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(VersionFile)) File.Delete(VersionFile); // not installed until everything below is in place

            var tgz = Path.Combine(Directory, "playwright-core.tgz");
            var unpackDir = Path.Combine(Directory, "unpack");
            try
            {
                var part = await DownloadAsync(_sources.DriverUrl, _sources.DriverSha256, "playwright-core.tgz", progress, ct)
                    .ConfigureAwait(false);
                File.Move(part, tgz, overwrite: true);

                progress?.Report(new ToolSetupProgress(ToolName, ToolSetupStage.Extracting));
                if (System.IO.Directory.Exists(unpackDir)) System.IO.Directory.Delete(unpackDir, recursive: true);
                await ExtractTgzAsync(tgz, unpackDir, ct).ConfigureAwait(false);
                var unpackedPackage = Path.Combine(unpackDir, "package"); // npm tarballs keep everything under package/
                if (!File.Exists(Path.Combine(unpackedPackage, "cli.js")))
                    throw new ToolSetupException("Couldn't unpack the browser helper: playwright-core has no cli.js.");
                System.IO.Directory.CreateDirectory(DriverRoot);
                if (System.IO.Directory.Exists(PackageDirectory)) System.IO.Directory.Delete(PackageDirectory, recursive: true);
                System.IO.Directory.Move(unpackedPackage, PackageDirectory);

                part = await DownloadAsync(_sources.NodeUrl, _sources.NodeSha256, "node.exe", progress, ct).ConfigureAwait(false);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(NodePath)!);
                File.Move(part, NodePath, overwrite: true);
            }
            finally
            {
                TryDelete(() => System.IO.Directory.Delete(unpackDir, recursive: true), unpackDir);
                TryDelete(() => File.Delete(tgz), tgz);
            }

            File.WriteAllText(VersionFile, VersionText);
            progress?.Report(new ToolSetupProgress(ToolName, ToolSetupStage.Installed));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unpacks a .tgz's regular files into <paramref name="target"/>. An entry that would land outside it fails setup.</summary>
    private static async Task ExtractTgzAsync(string tgz, string target, CancellationToken ct)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        await using var file = File.OpenRead(tgz);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var destination = Path.GetFullPath(Path.Combine(target, entry.Name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ToolSetupException($"Couldn't unpack the browser helper: \"{entry.Name}\" points outside its folder.");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await entry.ExtractToFileAsync(destination, overwrite: true, ct).ConfigureAwait(false);
        }
    }

    private Task<string> DownloadAsync(string url, string sha256, string asset, IProgress<ToolSetupProgress>? progress, CancellationToken ct) =>
        ToolDownloads.DownloadVerifiedAsync(_http, ToolName, url, Path.Combine(Directory, asset + ".part"), sha256, asset,
            StallTimeout, progress, ct);

    private static void TryDelete(Action delete, string path)
    {
        if (!Path.Exists(path)) return;
        try { delete(); }
        catch (IOException) { /* still in use; the next setup overwrites it */ }
    }
}
