using System.Collections.Concurrent;
using System.Security.Cryptography;
using TurboDL.Core.Archives;
using TurboDL.Core.Engine;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

public sealed class SevenZipToolsTests : IAsyncLifetime
{
    private const string Repo = "test/7zip";
    private const string Tag = "26.05";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TurboDL.SevenZipToolsTests." + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = TurboHttp.CreateClient();
    private readonly byte[] _fake7zr = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FakeSevenZip.exe"));
    // Fixed lower-case text, so it can't contain one of FakeSevenZip's upper-case markers (FAIL, WARN, SLOW...) by chance.
    private readonly byte[] _installer = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("fake 7-zip installer ", 5000)));
    private FakeGitHub _gh = null!;

    public async Task InitializeAsync() => _gh = await FakeGitHub.StartAsync();

    public async Task DisposeAsync()
    {
        await _gh.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Publishes the fake 7zr.exe and the given installer bytes, and returns a SevenZipTools pointed at them.</summary>
    private SevenZipTools Setup(byte[] installer, string? standaloneSha256 = null)
    {
        var sources = new SevenZipSources
        {
            GitHubBase = _gh.Base,
            Repo = Repo,
            Tag = Tag,
            StandaloneSha256 = standaloneSha256 ?? Sha256(_fake7zr),
            InstallerSha256 = Sha256(installer),
        };
        _gh.Assets[$"{Repo}/{Tag}/{sources.StandaloneAsset}"] = _fake7zr;
        _gh.Assets[$"{Repo}/{Tag}/{sources.InstallerAsset}"] = installer;

        var tools = new SevenZipTools(_dir, _http, sources);
        // The downloaded 7zr.exe is the fake's apphost, which needs its dll and runtime files next to it.
        Directory.CreateDirectory(tools.Directory);
        foreach (var ext in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FakeSevenZip" + ext), Path.Combine(tools.Directory, "FakeSevenZip" + ext), true);
        return tools;
    }

    private static void AssertCleanedUp(SevenZipTools tools)
    {
        var sources = new SevenZipSources();
        Assert.False(File.Exists(Path.Combine(tools.Directory, sources.StandaloneAsset)));
        Assert.False(File.Exists(Path.Combine(tools.Directory, sources.InstallerAsset)));
        Assert.False(Directory.Exists(Path.Combine(tools.Directory, "unpack")));
        Assert.Empty(Directory.GetFiles(tools.Directory, "*.part"));
    }

    [Fact]
    public async Task Ensure_DownloadsVerifiesUnpacksAndCleansUp()
    {
        var tools = Setup(_installer);
        var reports = new ConcurrentQueue<ToolSetupProgress>();

        await tools.EnsureAsync(new InlineProgress(reports.Enqueue));

        Assert.True(tools.IsInstalled);
        Assert.Equal(Path.Combine(_dir, "7zip", "7z.exe"), tools.SevenZipPath);
        Assert.Equal("dummy 7z.exe", await File.ReadAllTextAsync(tools.SevenZipPath));
        Assert.Equal("dummy 7z.dll", await File.ReadAllTextAsync(Path.Combine(tools.Directory, "7z.dll")));
        Assert.Equal(Tag, await File.ReadAllTextAsync(Path.Combine(tools.Directory, "version.txt")));
        AssertCleanedUp(tools);

        // 7zr was given exactly the installer-unpack arguments.
        var installerPath = Path.Combine(tools.Directory, new SevenZipSources().InstallerAsset);
        Assert.Equal(["x", "-y", "-o" + Path.Combine(tools.Directory, "unpack"), "--", installerPath, "7z.exe", "7z.dll"],
            await File.ReadAllLinesAsync(installerPath + ".args"));

        Assert.Contains(reports, r => r is { Tool: "7-Zip", Stage: ToolSetupStage.Verifying });
        Assert.Contains(reports, r => r is { Tool: "7-Zip", Stage: ToolSetupStage.Extracting });
        Assert.Equal(ToolSetupStage.Installed, reports.Last().Stage);
    }

    [Fact]
    public async Task Ensure_WhenInstalled_DoesNothing()
    {
        var tools = Setup(_installer);
        await tools.EnsureAsync();
        var before = _gh.Requests.Count;

        await tools.EnsureAsync();

        Assert.True(tools.IsInstalled);
        Assert.Equal(before, _gh.Requests.Count);
    }

    [Fact]
    public async Task Ensure_ChecksumMismatch_Throws_AndInstallsNothing()
    {
        var tools = Setup(_installer, standaloneSha256: new string('0', 64));

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() => tools.EnsureAsync());

        Assert.Contains("7zr.exe", ex.Message);
        Assert.False(tools.IsInstalled);
        AssertCleanedUp(tools);
        // The installer was never fetched, so nothing unverified could have run.
        Assert.DoesNotContain(_gh.Requests, r => r.StartsWith(new SevenZipSources().InstallerAsset, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_UnpackFails_Throws()
    {
        var installer = "FAIL"u8.ToArray().Concat(_installer).ToArray();
        var tools = Setup(installer);

        var ex = await Assert.ThrowsAsync<ToolSetupException>(() => tools.EnsureAsync());

        Assert.StartsWith("Couldn't unpack 7-Zip: ", ex.Message);
        Assert.Contains("Data Error", ex.Message);
        Assert.False(tools.IsInstalled);
        Assert.False(File.Exists(Path.Combine(tools.Directory, "version.txt")));
        AssertCleanedUp(tools);
    }

    private sealed class InlineProgress(Action<ToolSetupProgress> report) : IProgress<ToolSetupProgress>
    {
        public void Report(ToolSetupProgress value) => report(value);
    }
}
