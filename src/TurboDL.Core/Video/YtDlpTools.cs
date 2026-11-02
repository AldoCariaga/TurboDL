using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TurboDL.Core.Engine;

namespace TurboDL.Core.Video;

/// <summary>Where the tools come from: GitHub releases, each verified against a checksums file in the same release.</summary>
public sealed class ToolSources
{
    /// <summary>Base of the release URLs (<c>{base}/{repo}/releases/latest</c>, <c>{base}/{repo}/releases/download/{tag}/{asset}</c>).</summary>
    public string GitHubBase { get; init; } = "https://github.com";

    public string YtDlpRepo { get; init; } = "yt-dlp/yt-dlp";
    public string YtDlpAsset { get; init; } = "yt-dlp.exe";
    public string YtDlpChecksums { get; init; } = "SHA2-256SUMS";

    public string FfmpegRepo { get; init; } = "yt-dlp/FFmpeg-Builds";
    public string FfmpegAsset { get; init; } = "ffmpeg-master-latest-win64-gpl.zip";
    public string FfmpegChecksums { get; init; } = "checksums.sha256";

    public string DenoRepo { get; init; } = "denoland/deno";
    public string DenoAsset { get; init; } = "deno-x86_64-pc-windows-msvc.zip";
    public string DenoChecksums { get; init; } = "deno-x86_64-pc-windows-msvc.zip.sha256sum";
}

public enum ToolSetupStage
{
    Downloading,
    Verifying,
    Extracting,
    Installed,
}

/// <param name="Tool">"yt-dlp", "ffmpeg" or "deno".</param>
public sealed record ToolSetupProgress(string Tool, ToolSetupStage Stage, long BytesReceived = 0, long? TotalBytes = null);

/// <summary>Setting up or updating the video tools failed.</summary>
public class ToolSetupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A downloaded tool didn't match the checksum its release publishes. The file was deleted.</summary>
public sealed class ChecksumMismatchException(string fileName, string expected, string actual)
    : ToolSetupException($"{fileName} doesn't match its published SHA-256 (expected {expected}, got {actual}). The download was discarded.")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}

/// <summary>What <see cref="YtDlpTools.UpdateAsync"/> did.</summary>
public sealed record ToolUpdateResult
{
    /// <summary>False when the last check was less than <see cref="YtDlpTools.UpdateInterval"/> ago.</summary>
    public bool Ran { get; init; }

    /// <summary>True when <c>yt-dlp -U</c> exited with 0.</summary>
    public bool YtDlpOk { get; init; }

    /// <summary>What <c>yt-dlp -U</c> printed (stdout and stderr), e.g. "yt-dlp is up to date".</summary>
    public string? YtDlpOutput { get; init; }

    /// <summary>The Deno release now installed.</summary>
    public string? DenoVersion { get; init; }

    public bool DenoUpdated { get; init; }

    /// <summary>Why the Deno update failed, if it did.</summary>
    public string? DenoError { get; init; }

    public static ToolUpdateResult Skipped { get; } = new();
}

/// <summary>
/// The yt-dlp tool folder: yt-dlp.exe, ffmpeg.exe + ffprobe.exe (yt-dlp's FFmpeg-Builds) and deno.exe
/// (the JavaScript runtime yt-dlp needs for YouTube). Installs what's missing and keeps it current.
/// </summary>
public sealed class YtDlpTools
{
    private const string VersionsFile = "versions.json";
    private const int BufferSize = 1 << 16;

    private readonly HttpClient _http;
    private readonly ToolSources _sources;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL", "tools");

    public YtDlpTools(string? directory = null, HttpClient? http = null, ToolSources? sources = null)
    {
        Directory = directory ?? DefaultDirectory;
        _http = http ?? TurboHttp.CreateClient();
        _sources = sources ?? new ToolSources();
    }

    public string Directory { get; }
    public string YtDlpPath => Path.Combine(Directory, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(Directory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(Directory, "ffprobe.exe");
    public string DenoPath => Path.Combine(Directory, "deno.exe");

    public bool IsInstalled =>
        File.Exists(YtDlpPath) && File.Exists(FfmpegPath) && File.Exists(FfprobePath) && File.Exists(DenoPath);

    /// <summary>How often <see cref="UpdateAsync"/> actually checks (default once a day).</summary>
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>A download that receives nothing for this long is abandoned (and can be resumed by calling again).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for <c>yt-dlp -U</c>.</summary>
    public TimeSpan SelfUpdateTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Downloads, verifies and installs whichever tools are missing. An interrupted download leaves a
    /// <c>.part</c> file that the next call resumes; a file that fails its checksum is deleted, never installed.
    /// </summary>
    public async Task EnsureAsync(IProgress<ToolSetupProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var versions = LoadVersions();

            if (!File.Exists(YtDlpPath))
            {
                var tag = await ResolveLatestTagAsync(_sources.YtDlpRepo, ct).ConfigureAwait(false);
                var part = await DownloadVerifiedAsync("yt-dlp", _sources.YtDlpRepo, tag, _sources.YtDlpAsset,
                    _sources.YtDlpChecksums, progress, ct).ConfigureAwait(false);
                File.Move(part, YtDlpPath, overwrite: true);
                versions.YtDlp = tag;
                SaveVersions(versions);
                progress?.Report(new ToolSetupProgress("yt-dlp", ToolSetupStage.Installed));
            }

            if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
            {
                var tag = await ResolveLatestTagAsync(_sources.FfmpegRepo, ct).ConfigureAwait(false);
                var zip = await DownloadVerifiedAsync("ffmpeg", _sources.FfmpegRepo, tag, _sources.FfmpegAsset,
                    _sources.FfmpegChecksums, progress, ct).ConfigureAwait(false);
                progress?.Report(new ToolSetupProgress("ffmpeg", ToolSetupStage.Extracting));
                ExtractAndDelete(zip, ("ffmpeg.exe", FfmpegPath), ("ffprobe.exe", FfprobePath));
                versions.Ffmpeg = tag;
                SaveVersions(versions);
                progress?.Report(new ToolSetupProgress("ffmpeg", ToolSetupStage.Installed));
            }

            if (!File.Exists(DenoPath))
            {
                var tag = await ResolveLatestTagAsync(_sources.DenoRepo, ct).ConfigureAwait(false);
                await InstallDenoAsync(tag, progress, ct).ConfigureAwait(false);
                versions.Deno = tag;
                SaveVersions(versions);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// At most once per <see cref="UpdateInterval"/> (unless <paramref name="force"/>): runs <c>yt-dlp -U</c>
    /// and replaces deno.exe when a newer Deno release is out. Don't call it while a yt-dlp download is running.
    /// </summary>
    public async Task<ToolUpdateResult> UpdateAsync(bool force = false, IProgress<ToolSetupProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsInstalled) throw new InvalidOperationException("The video tools aren't installed yet; call EnsureAsync first.");

            var versions = LoadVersions();
            var now = DateTime.UtcNow;
            if (!force && versions.LastUpdateCheckUtc is { } last && now - last < UpdateInterval && now >= last)
                return ToolUpdateResult.Skipped;

            // Record the attempt first, so a failing update isn't retried on every call.
            versions.LastUpdateCheckUtc = now;
            SaveVersions(versions);

            var output = new List<string>();
            var run = await ToolProcess.RunAsync(YtDlpPath, ["-U"], output.Add, output.Add, SelfUpdateTimeout, ct)
                .ConfigureAwait(false);
            var ytDlpOutput = run.TimedOut
                ? $"yt-dlp -U didn't finish within {SelfUpdateTimeout.TotalMinutes:0} minutes."
                : string.Join(Environment.NewLine, output.Where(l => l.Length > 0));

            string? denoVersion = versions.Deno;
            var denoUpdated = false;
            string? denoError = null;
            try
            {
                var tag = await ResolveLatestTagAsync(_sources.DenoRepo, ct).ConfigureAwait(false);
                if (!string.Equals(tag, versions.Deno, StringComparison.Ordinal))
                {
                    await InstallDenoAsync(tag, progress, ct).ConfigureAwait(false);
                    versions.Deno = denoVersion = tag;
                    SaveVersions(versions);
                    denoUpdated = true;
                }
            }
            catch (Exception ex) when (ex is ToolSetupException or HttpRequestException or IOException or UnauthorizedAccessException
                                           or InvalidDataException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                denoError = ex.Message;
            }

            return new ToolUpdateResult
            {
                Ran = true,
                YtDlpOk = !run.TimedOut && run.ExitCode == 0,
                YtDlpOutput = ytDlpOutput,
                DenoVersion = denoVersion,
                DenoUpdated = denoUpdated,
                DenoError = denoError,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InstallDenoAsync(string tag, IProgress<ToolSetupProgress>? progress, CancellationToken ct)
    {
        var zip = await DownloadVerifiedAsync("deno", _sources.DenoRepo, tag, _sources.DenoAsset, _sources.DenoChecksums,
            progress, ct).ConfigureAwait(false);
        progress?.Report(new ToolSetupProgress("deno", ToolSetupStage.Extracting));
        ExtractAndDelete(zip, ("deno.exe", DenoPath));
        progress?.Report(new ToolSetupProgress("deno", ToolSetupStage.Installed));
    }

    /// <summary>The tag <c>/releases/latest</c> redirects to, so the asset and its checksums come from the same release.</summary>
    private async Task<string> ResolveLatestTagAsync(string repo, CancellationToken ct)
    {
        var url = $"{_sources.GitHubBase}/{repo}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TurboHttp.ApplyHeaders(request, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ToolSetupException($"Couldn't find the latest {repo} release (HTTP {(int)response.StatusCode}).");

        var final = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        const string marker = "/releases/tag/";
        var at = final.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new ToolSetupException($"Couldn't find the latest {repo} release (no release tag in {final}).");
        return Uri.UnescapeDataString(final[(at + marker.Length)..].TrimEnd('/'));
    }

    private string ReleaseUrl(string repo, string tag, string asset) =>
        $"{_sources.GitHubBase}/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";

    /// <summary>Downloads an asset to <c>{asset}.part</c> (resuming a previous attempt), checks its SHA-256, and returns the path.</summary>
    private async Task<string> DownloadVerifiedAsync(string tool, string repo, string tag, string asset, string checksumsAsset,
        IProgress<ToolSetupProgress>? progress, CancellationToken ct)
    {
        var sums = await GetStringAsync(ReleaseUrl(repo, tag, checksumsAsset), ct).ConfigureAwait(false);
        var expected = Checksums.FindSha256(sums, asset)
            ?? throw new ToolSetupException($"{checksumsAsset} in {repo} {tag} doesn't list {asset}.");

        var part = Path.Combine(Directory, asset + ".part");
        var url = ReleaseUrl(repo, tag, asset);
        for (var attempt = 0; ; attempt++)
        {
            var resumed = await DownloadToPartAsync(tool, url, part, progress, ct).ConfigureAwait(false);
            progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Verifying));
            var actual = await Checksums.Sha256OfFileAsync(part, ct).ConfigureAwait(false);
            if (actual == expected) return part;

            File.Delete(part);
            // A leftover .part may belong to an older build of the same file name; one clean retry settles it.
            if (!resumed || attempt > 0) throw new ChecksumMismatchException(asset, expected, actual);
        }
    }

    /// <summary>Returns true when it carried on from an existing partial file.</summary>
    private async Task<bool> DownloadToPartAsync(string tool, string url, string part, IProgress<ToolSetupProgress>? progress,
        CancellationToken ct)
    {
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TurboHttp.ApplyHeaders(request, null);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (response.Content.Headers.ContentRange?.Length == existing) return true; // Already complete.
            // Longer than the file on the server: a leftover from something else. Start over.
            File.Delete(part);
            return await DownloadToPartAsync(tool, url, part, progress, ct).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
            throw new ToolSetupException($"Downloading {Path.GetFileName(url)} failed: HTTP {(int)response.StatusCode}.");

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        long? total = response.Content.Headers.ContentRange?.Length
                      ?? (response.Content.Headers.ContentLength is { } len ? existing + len : null);

        await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, BufferSize, useAsync: true);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        var received = existing;
        var lastReport = DateTime.MinValue;
        progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Downloading, received, total));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int n;
            try
            {
                n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolSetupException(
                    $"Downloading {Path.GetFileName(url)} stalled (nothing for {StallTimeout.TotalSeconds:0} s). Try again to resume.");
            }
            if (n == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            received += n;
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Downloading, received, total));
            }
        }
        progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Downloading, received, total));
        if (total is { } t && received != t)
            throw new ToolSetupException($"Downloading {Path.GetFileName(url)} ended early ({received:N0} of {t:N0} bytes). Try again to resume.");
        return resumed;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TurboHttp.ApplyHeaders(request, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StallTimeout);
        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ToolSetupException($"Downloading {Path.GetFileName(url)} failed: HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolSetupException($"Downloading {Path.GetFileName(url)} timed out.");
        }
    }

    /// <summary>Pulls the named files out of a zip (from any folder inside it), installs them, then deletes the zip.</summary>
    private static void ExtractAndDelete(string zipPath, params (string EntryName, string Destination)[] files)
    {
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var (entryName, destination) in files)
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolSetupException($"{Path.GetFileName(zipPath)} doesn't contain {entryName}.");
                var temp = destination + ".tmp";
                entry.ExtractToFile(temp, overwrite: true);
                File.Move(temp, destination, overwrite: true);
            }
        }
        File.Delete(zipPath);
    }

    private sealed class ToolVersions
    {
        public string? YtDlp { get; set; }
        public string? Ffmpeg { get; set; }
        public string? Deno { get; set; }
        public DateTime? LastUpdateCheckUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private ToolVersions LoadVersions()
    {
        try
        {
            var path = Path.Combine(Directory, VersionsFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ToolVersions>(File.ReadAllText(path), JsonOptions) ?? new ToolVersions()
                : new ToolVersions();
        }
        catch (JsonException)
        {
            // A damaged record only costs an extra update check.
            return new ToolVersions();
        }
    }

    private void SaveVersions(ToolVersions versions)
    {
        var path = Path.Combine(Directory, VersionsFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(versions, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
