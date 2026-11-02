using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TurboDL.Core.Engine;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

/// <summary>Plays github.com: <c>/{repo}/releases/latest</c> redirects to the tag, assets are served with Range support.</summary>
public sealed class FakeGitHub : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>repo → latest tag.</summary>
    public ConcurrentDictionary<string, string> Latest { get; } = new();

    /// <summary>"{repo}/{tag}/{asset}" → bytes.</summary>
    public ConcurrentDictionary<string, byte[]> Assets { get; } = new();

    /// <summary>"{asset} {Range header}" of every asset request.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public string Base { get; private set; } = "";

    private FakeGitHub()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/{owner}/{repo}/releases/latest", (string owner, string repo) =>
            Latest.TryGetValue($"{owner}/{repo}", out var tag)
                ? Results.Redirect($"/{owner}/{repo}/releases/tag/{tag}")
                : Results.NotFound());
        _app.MapGet("/{owner}/{repo}/releases/tag/{tag}", () => Results.Text("release page"));
        _app.MapGet("/{owner}/{repo}/releases/download/{tag}/{asset}", (string owner, string repo, string tag, string asset, HttpContext ctx) =>
        {
            Requests.Enqueue($"{asset} {ctx.Request.Headers.Range}");
            return Assets.TryGetValue($"{owner}/{repo}/{tag}/{asset}", out var bytes)
                ? Results.File(bytes, "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound();
        });
    }

    public static async Task<FakeGitHub> StartAsync()
    {
        var gh = new FakeGitHub();
        await gh._app.StartAsync();
        gh.Base = gh._app.Urls.First();
        return gh;
    }

    /// <summary>Publishes a release: the assets plus a checksums file listing them.</summary>
    public void Publish(string repo, string tag, string checksumsName, IDictionary<string, byte[]> files, Func<string, string, string>? lineFormat = null)
    {
        Latest[repo] = tag;
        var sums = new System.Text.StringBuilder();
        foreach (var (name, bytes) in files)
        {
            Assets[$"{repo}/{tag}/{name}"] = bytes;
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            sums.Append(lineFormat?.Invoke(hash, name) ?? $"{hash}  {name}\n");
        }
        Assets[$"{repo}/{tag}/{checksumsName}"] = System.Text.Encoding.UTF8.GetBytes(sums.ToString());
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public sealed class YtDlpToolsTests : IAsyncLifetime
{
    private static readonly ToolSources Sources0 = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TurboDL.ToolsTests." + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = TurboHttp.CreateClient();
    private FakeGitHub _gh = null!;
    private ToolSources _sources = null!;

    private readonly byte[] _ytDlp = TestFileServer.RandomBytes(300_000);
    private readonly byte[] _ffmpeg = TestFileServer.RandomBytes(400_001);
    private readonly byte[] _ffprobe = TestFileServer.RandomBytes(200_003);
    private readonly byte[] _deno = TestFileServer.RandomBytes(250_007);

    public async Task InitializeAsync()
    {
        _gh = await FakeGitHub.StartAsync();
        _sources = new ToolSources
        {
            GitHubBase = _gh.Base,
            YtDlpRepo = "yt/ytdlp",
            FfmpegRepo = "yt/ffmpeg",
            DenoRepo = "dn/deno",
        };
        _gh.Publish(_sources.YtDlpRepo, "2026.10.26", Sources0.YtDlpChecksums, new Dictionary<string, byte[]>
        {
            ["yt-dlp"] = [1, 2, 3],
            [Sources0.YtDlpAsset] = _ytDlp,
        });
        _gh.Publish(_sources.FfmpegRepo, "latest", Sources0.FfmpegChecksums, new Dictionary<string, byte[]>
        {
            [Sources0.FfmpegAsset] = Zip(("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", _ffmpeg),
                ("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", _ffprobe), ("ffmpeg-master-latest-win64-gpl/LICENSE.txt", [9])),
        });
        PublishDeno("v2.9.8", _deno);
    }

    public async Task DisposeAsync()
    {
        await _gh.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void PublishDeno(string tag, byte[] exe) =>
        _gh.Publish(_sources.DenoRepo, tag, Sources0.DenoChecksums,
            new Dictionary<string, byte[]> { [Sources0.DenoAsset] = Zip(("deno.exe", exe)) },
            // Deno publishes PowerShell Get-FileHash output.
            (hash, _) => $"\r\nAlgorithm : SHA256\r\nHash      : {hash.ToUpperInvariant()}\r\nPath      : C:\\a\\deno\\{Sources0.DenoAsset}\r\n\r\n");

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    private YtDlpTools Tools(TimeSpan? interval = null) =>
        new(_dir, _http, _sources) { UpdateInterval = interval ?? TimeSpan.FromDays(1) };

    [Fact]
    public async Task Ensure_InstallsAllFour_Verified_AndCleansUp()
    {
        var tools = Tools();
        var reports = new ConcurrentQueue<ToolSetupProgress>();
        await tools.EnsureAsync(new InlineProgress(reports.Enqueue));

        Assert.True(tools.IsInstalled);
        Assert.Equal(_ytDlp, await File.ReadAllBytesAsync(tools.YtDlpPath));
        Assert.Equal(_ffmpeg, await File.ReadAllBytesAsync(tools.FfmpegPath));
        Assert.Equal(_ffprobe, await File.ReadAllBytesAsync(tools.FfprobePath));
        Assert.Equal(_deno, await File.ReadAllBytesAsync(tools.DenoPath));
        // Only the tools and the version record are left: no zips, .part or .tmp files, no LICENSE.
        Assert.Equal(["deno.exe", "ffmpeg.exe", "ffprobe.exe", "versions.json", "yt-dlp.exe"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
        Assert.Contains("2026.10.26", await File.ReadAllTextAsync(Path.Combine(_dir, "versions.json")));

        foreach (var tool in new[] { "yt-dlp", "ffmpeg", "deno" })
        {
            Assert.Contains(reports, r => r.Tool == tool && r.Stage == ToolSetupStage.Installed);
            Assert.Contains(reports, r => r.Tool == tool && r.Stage == ToolSetupStage.Verifying);
        }
        var ffmpegDl = reports.Where(r => r.Tool == "ffmpeg" && r.Stage == ToolSetupStage.Downloading).ToList();
        Assert.Equal(ffmpegDl[^1].TotalBytes, ffmpegDl[^1].BytesReceived);

        // A second call has nothing to do.
        var before = _gh.Requests.Count;
        await tools.EnsureAsync();
        Assert.Equal(before, _gh.Requests.Count);
    }

    [Fact]
    public async Task Ensure_RejectsAFileThatDoesNotMatchItsChecksum()
    {
        // The release lists one hash but serves different bytes.
        _gh.Assets[$"{_sources.YtDlpRepo}/2026.10.26/{Sources0.YtDlpAsset}"] = TestFileServer.RandomBytes(1234);
        var tools = Tools();

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() => tools.EnsureAsync());
        Assert.Contains("yt-dlp.exe", ex.Message);
        Assert.False(File.Exists(tools.YtDlpPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task Ensure_FailsWhenTheChecksumsFileDoesNotListTheAsset()
    {
        _gh.Assets[$"{_sources.YtDlpRepo}/2026.10.26/{Sources0.YtDlpChecksums}"] = "abc  other.exe\n"u8.ToArray();
        await Assert.ThrowsAsync<ToolSetupException>(() => Tools().EnsureAsync());
        Assert.False(File.Exists(Path.Combine(_dir, "yt-dlp.exe")));
    }

    [Fact]
    public async Task Ensure_ResumesAnInterruptedDownload()
    {
        Directory.CreateDirectory(_dir);
        var zip = _gh.Assets[$"{_sources.FfmpegRepo}/latest/{Sources0.FfmpegAsset}"];
        await File.WriteAllBytesAsync(Path.Combine(_dir, Sources0.FfmpegAsset + ".part"), zip[..(zip.Length / 2)]);

        var tools = Tools();
        await tools.EnsureAsync();

        Assert.Equal(_ffmpeg, await File.ReadAllBytesAsync(tools.FfmpegPath));
        Assert.Contains($"{Sources0.FfmpegAsset} bytes={zip.Length / 2}-", _gh.Requests);
    }

    [Fact]
    public async Task Ensure_StaleLeftover_IsDiscarded_AndDownloadedAgain()
    {
        Directory.CreateDirectory(_dir);
        // A leftover from some other build: same name, wrong bytes.
        await File.WriteAllBytesAsync(Path.Combine(_dir, Sources0.YtDlpAsset + ".part"), TestFileServer.RandomBytes(1000));

        var tools = Tools();
        await tools.EnsureAsync();

        Assert.Equal(_ytDlp, await File.ReadAllBytesAsync(tools.YtDlpPath));
        Assert.Contains($"{Sources0.YtDlpAsset} bytes=1000-", _gh.Requests);
        Assert.Contains($"{Sources0.YtDlpAsset} ", _gh.Requests);
    }

    [Fact]
    public async Task Ensure_LeftoverLongerThanTheFile_StartsOver()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, Sources0.YtDlpAsset + ".part"), TestFileServer.RandomBytes(_ytDlp.Length + 10));

        var tools = Tools();
        await tools.EnsureAsync();

        Assert.Equal(_ytDlp, await File.ReadAllBytesAsync(tools.YtDlpPath));
    }

    [Fact]
    public async Task Update_RunsYtDlpSelfUpdate_AndReplacesDeno_OncePerInterval()
    {
        var tools = Tools();
        await tools.EnsureAsync();
        // Swap in the fake yt-dlp, which answers -U.
        using (var fake = new FakeToolsFolder())
        {
            foreach (var f in Directory.GetFiles(fake.Tools, "FakeYtDlp.*")) File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)), true);
            File.Copy(Path.Combine(fake.Tools, "yt-dlp.exe"), tools.YtDlpPath, overwrite: true);
        }
        var newDeno = TestFileServer.RandomBytes(260_000);
        PublishDeno("v2.9.9", newDeno);

        var first = await tools.UpdateAsync();
        Assert.True(first.Ran);
        Assert.True(first.YtDlpOk);
        Assert.Contains("yt-dlp is up to date", first.YtDlpOutput);
        Assert.True(first.DenoUpdated);
        Assert.Equal("v2.9.9", first.DenoVersion);
        Assert.Null(first.DenoError);
        Assert.Equal(newDeno, await File.ReadAllBytesAsync(tools.DenoPath));

        // Within the interval: nothing runs.
        var second = await tools.UpdateAsync();
        Assert.False(second.Ran);

        // Forced, with Deno current: yt-dlp runs, Deno stays.
        var forced = await tools.UpdateAsync(force: true);
        Assert.True(forced.Ran);
        Assert.False(forced.DenoUpdated);
        Assert.Equal("v2.9.9", forced.DenoVersion);

        // A zero interval behaves like "always check".
        Assert.True((await Tools(TimeSpan.Zero).UpdateAsync()).Ran);
    }

    [Fact]
    public async Task Update_BeforeEnsure_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tools().UpdateAsync());
    }

    private sealed class InlineProgress(Action<ToolSetupProgress> report) : IProgress<ToolSetupProgress>
    {
        public void Report(ToolSetupProgress value) => report(value);
    }
}
