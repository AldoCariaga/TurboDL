using TurboDL.Core.Engine;
using TurboDL.Core.Video;

namespace TurboDL.Core.Archives;

/// <summary>Where 7-Zip comes from: one pinned GitHub release, each file checked against its SHA-256 (the release has no checksums file).</summary>
public sealed class SevenZipSources
{
    /// <summary>Base of the release URLs (<c>{base}/{repo}/releases/download/{tag}/{asset}</c>).</summary>
    public string GitHubBase { get; init; } = "https://github.com";

    public string Repo { get; init; } = "ip7z/7zip";
    public string Tag { get; init; } = "26.05";

    /// <summary>The standalone 7zr.exe, which only reads .7z; it is used once, to unpack the installer.</summary>
    public string StandaloneAsset { get; init; } = "7zr.exe";
    public string StandaloneSha256 { get; init; } = "9bf582b894dfa665f852d7e9112b02290a1c303c2d95a21fcdc41ffcb03143b7";

    /// <summary>The installer, a 7z self-extractor that holds the full 7z.exe and 7z.dll (with RAR support).</summary>
    public string InstallerAsset { get; init; } = "7z2605-x64.exe";
    public string InstallerSha256 { get; init; } = "fd99e86f57d8f429ef5ed045bf98315af962e2309b0f1ba87a147a44c71647d0";
}

/// <summary>
/// 7-Zip for extracting RAR/7z/zip archives, downloaded on first use into &lt;tools&gt;\7zip. 7zr.exe (verified) unpacks
/// 7z.exe and 7z.dll from the verified installer; 7zr and the installer are then deleted. Nothing is installed system-wide.
/// </summary>
public sealed class SevenZipTools
{
    /// <summary>How much setup downloads, for the user-facing prompt.</summary>
    public const string SetupSizeText = "about 2 MB";

    private const string ToolName = "7-Zip";

    private readonly HttpClient _http;
    private readonly SevenZipSources _sources;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SevenZipTools(string? toolsDirectory = null, HttpClient? http = null, SevenZipSources? sources = null)
    {
        Directory = Path.Combine(toolsDirectory ?? YtDlpTools.DefaultDirectory, "7zip");
        _http = http ?? TurboHttp.CreateClient();
        _sources = sources ?? new SevenZipSources();
    }

    /// <summary>&lt;tools&gt;\7zip (tools defaults to <see cref="YtDlpTools.DefaultDirectory"/>).</summary>
    public string Directory { get; }

    /// <summary>Directory\7z.exe.</summary>
    public string SevenZipPath => Path.Combine(Directory, "7z.exe");

    private string SevenZipDllPath => Path.Combine(Directory, "7z.dll");

    /// <summary>True when 7z.exe and 7z.dll are both in place.</summary>
    public bool IsInstalled => File.Exists(SevenZipPath) && File.Exists(SevenZipDllPath);

    /// <summary>A download that receives nothing for this long is abandoned (and can be resumed by calling again).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for unpacking 7z.exe and 7z.dll from the installer.</summary>
    public TimeSpan UnpackTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Installs 7-Zip if it's missing. Both downloads are checked against their pinned SHA-256 before anything runs.
    /// An interrupted download leaves a <c>.part</c> file that the next call resumes; a file that fails its checksum is deleted.
    /// </summary>
    public async Task EnsureAsync(IProgress<ToolSetupProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled) return;
            System.IO.Directory.CreateDirectory(Directory);

            var standalone = Path.Combine(Directory, _sources.StandaloneAsset);
            var installer = Path.Combine(Directory, _sources.InstallerAsset);
            var unpackDir = Path.Combine(Directory, "unpack");
            try
            {
                var part = await DownloadAsync(_sources.StandaloneAsset, _sources.StandaloneSha256, progress, ct).ConfigureAwait(false);
                File.Move(part, standalone, overwrite: true);

                part = await DownloadAsync(_sources.InstallerAsset, _sources.InstallerSha256, progress, ct).ConfigureAwait(false);
                File.Move(part, installer, overwrite: true);

                progress?.Report(new ToolSetupProgress(ToolName, ToolSetupStage.Extracting));
                if (System.IO.Directory.Exists(unpackDir)) System.IO.Directory.Delete(unpackDir, recursive: true);

                var run = await ToolProcess.RunAsync(standalone,
                        ["x", "-y", "-o" + unpackDir, "--", installer, "7z.exe", "7z.dll"], null, null, UnpackTimeout, ct)
                    .ConfigureAwait(false);
                var unpackedExe = Path.Combine(unpackDir, "7z.exe");
                var unpackedDll = Path.Combine(unpackDir, "7z.dll");
                if (run.TimedOut)
                    throw new ToolSetupException("Couldn't unpack 7-Zip: it didn't finish in time.");
                if (run.ExitCode != 0)
                {
                    var detail = run.StdErr.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? $"exit code {run.ExitCode}";
                    throw new ToolSetupException($"Couldn't unpack 7-Zip: {detail}");
                }
                if (!File.Exists(unpackedExe) || !File.Exists(unpackedDll))
                    throw new ToolSetupException("Couldn't unpack 7-Zip: the installer didn't contain 7z.exe and 7z.dll.");

                // 7z.exe last, so IsInstalled only turns true once both are in place.
                File.Move(unpackedDll, SevenZipDllPath, overwrite: true);
                File.Move(unpackedExe, SevenZipPath, overwrite: true);
            }
            finally
            {
                TryDelete(() => System.IO.Directory.Delete(unpackDir, recursive: true), unpackDir);
                TryDelete(() => File.Delete(installer), installer);
                TryDelete(() => File.Delete(standalone), standalone);
            }

            File.WriteAllText(Path.Combine(Directory, "version.txt"), _sources.Tag);
            progress?.Report(new ToolSetupProgress(ToolName, ToolSetupStage.Installed));
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<string> DownloadAsync(string asset, string sha256, IProgress<ToolSetupProgress>? progress, CancellationToken ct)
    {
        var url = $"{_sources.GitHubBase}/{_sources.Repo}/releases/download/{Uri.EscapeDataString(_sources.Tag)}/{Uri.EscapeDataString(asset)}";
        return ToolDownloads.DownloadVerifiedAsync(_http, ToolName, url, Path.Combine(Directory, asset + ".part"), sha256, asset,
            StallTimeout, progress, ct);
    }

    private static void TryDelete(Action delete, string path)
    {
        if (!Path.Exists(path)) return;
        try { delete(); }
        catch (IOException) { /* still in use; the next setup overwrites it */ }
    }
}
