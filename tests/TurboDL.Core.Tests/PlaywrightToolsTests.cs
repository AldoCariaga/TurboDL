using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TurboDL.Core.Browser;
using TurboDL.Core.Engine;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

public sealed class PlaywrightToolsTests : IAsyncLifetime
{
    private const string Repo = "test/playwright";
    private const string Tag = "v1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TurboDL.PlaywrightToolsTests." + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = TurboHttp.CreateClient();
    private readonly byte[] _node = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("fake node ", 5000)));
    private FakeGitHub _gh = null!;

    public async Task InitializeAsync() => _gh = await FakeGitHub.StartAsync();

    public async Task DisposeAsync()
    {
        await _gh.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>An npm-style tarball: every file under package/.</summary>
    private static byte[] Tgz(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
        return buffer.ToArray();
    }

    private PlaywrightTools Setup(byte[] tgz, string driverVersion = "1.0.0")
    {
        _gh.Assets[$"{Repo}/{Tag}/driver.tgz"] = tgz;
        _gh.Assets[$"{Repo}/{Tag}/node.exe"] = _node;
        var sources = new PlaywrightSources
        {
            DriverVersion = driverVersion,
            DriverUrl = $"{_gh.Base}/{Repo}/releases/download/{Tag}/driver.tgz",
            DriverSha256 = Sha256(tgz),
            NodeVersion = "20.0.0",
            NodeUrl = $"{_gh.Base}/{Repo}/releases/download/{Tag}/node.exe",
            NodeSha256 = Sha256(_node),
        };
        return new PlaywrightTools(_dir, _http, sources);
    }

    [Fact]
    public async Task Ensure_LaysOutDriverWherePlaywrightLooks_AndCleansUp()
    {
        var tools = Setup(Tgz(("package/cli.js", "cli"), ("package/lib/server.js", "server")));

        await tools.EnsureAsync();

        Assert.True(tools.IsInstalled);
        Assert.Equal(Path.Combine(_dir, "playwright"), tools.Directory);
        Assert.Equal(Path.Combine(tools.Directory, ".playwright", "node", "win32_x64", "node.exe"), tools.NodePath);
        Assert.Equal(_node, await File.ReadAllBytesAsync(tools.NodePath));
        Assert.Equal("server", await File.ReadAllTextAsync(Path.Combine(tools.Directory, ".playwright", "package", "lib", "server.js")));
        Assert.False(Directory.Exists(Path.Combine(tools.Directory, "unpack")));
        Assert.False(File.Exists(Path.Combine(tools.Directory, "playwright-core.tgz")));
        Assert.Empty(Directory.GetFiles(tools.Directory, "*.part"));
    }

    [Fact]
    public async Task Ensure_WhenInstalled_DownloadsNothing()
    {
        var tools = Setup(Tgz(("package/cli.js", "cli")));
        await tools.EnsureAsync();
        var requests = _gh.Requests.Count;

        await tools.EnsureAsync();

        Assert.Equal(requests, _gh.Requests.Count);
    }

    [Fact]
    public async Task NewDriverVersion_IsNotInstalled_AndSetsUpAgain()
    {
        await Setup(Tgz(("package/cli.js", "old"))).EnsureAsync();

        var newer = Setup(Tgz(("package/cli.js", "new")), driverVersion: "2.0.0");
        Assert.False(newer.IsInstalled);
        await newer.EnsureAsync();

        Assert.True(newer.IsInstalled);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(newer.PackageDirectory, "cli.js")));
    }

    [Fact]
    public async Task EntryOutsideFolder_FailsSetup()
    {
        var tools = Setup(Tgz(("package/cli.js", "cli"), ("../../evil.txt", "x")));

        await Assert.ThrowsAsync<ToolSetupException>(() => tools.EnsureAsync());

        Assert.False(tools.IsInstalled);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public async Task WrongChecksum_FailsBeforeUnpacking()
    {
        var tgz = Tgz(("package/cli.js", "cli"));
        var tools = Setup(tgz);
        _gh.Assets[$"{Repo}/{Tag}/driver.tgz"] = Tgz(("package/cli.js", "tampered"));

        await Assert.ThrowsAsync<ChecksumMismatchException>(() => tools.EnsureAsync());

        Assert.False(tools.IsInstalled);
        Assert.False(Directory.Exists(tools.PackageDirectory));
    }
}
