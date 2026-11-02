using System.Diagnostics;
using System.Security.Cryptography;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;
using TurboDL.Core.Storage;

namespace TurboDL.Core.Tests;

public sealed class DownloadTaskTests : IDisposable
{
    private static readonly HttpClient Http = TurboHttp.CreateClient();

    private static readonly DownloadOptions FastOptions = new()
    {
        MinSegmentSize = 256 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        RetryMaxDelay = TimeSpan.FromMilliseconds(200),
        ReadTimeout = TimeSpan.FromSeconds(10),
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "turbodl-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DownloadInfo NewInfo(string url, int connections = 8) =>
        new() { Url = url, Directory = _dir, Connections = connections };

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task MultiSegmentDownload_MatchesSource()
    {
        var data = TestFileServer.RandomBytes(8 * 1024 * 1024 + 123);
        await using var server = await TestFileServer.StartAsync(data);
        var info = NewInfo(server.Url);

        await new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, info.Status);
        Assert.True(info.SupportsRanges);
        Assert.Equal(data.Length, info.TotalSize);
        Assert.True(info.Segments.Count >= 8, $"expected 8+ segments, got {info.Segments.Count}");
        Assert.Equal(Hash(data), Hash(info.FilePath));
        Assert.False(File.Exists(info.TempPath));
    }

    [Fact]
    public async Task SlowSegment_IsSplitAcrossIdleConnections()
    {
        // 4 connections but the file is laid out as one segment (as if resumed from a 1-connection run):
        // the 3 idle workers must split it instead of sitting idle.
        var data = TestFileServer.RandomBytes(4 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        server.ThrottleBytesPerSecond = 4 * 1024 * 1024;
        var info = NewInfo(server.Url, connections: 1);

        var first = new DownloadTask(info, Http, FastOptions);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(cts.Token));
        Assert.Single(info.Segments);

        info.Connections = 4;
        await new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None);

        Assert.True(info.Segments.Count > 1, "segment was never split");
        Assert.Equal(Hash(data), Hash(info.FilePath));
    }

    [Fact]
    public async Task ServerWithoutRanges_UsesOneConnection()
    {
        var data = TestFileServer.RandomBytes(3 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        server.SupportRanges = false;
        var info = NewInfo(server.Url);

        await new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None);

        Assert.False(info.SupportsRanges);
        Assert.Single(info.Segments);
        Assert.Equal(Hash(data), Hash(info.FilePath));
    }

    [Fact]
    public async Task UnknownSize_DownloadsToEndOfStream()
    {
        var data = TestFileServer.RandomBytes(1_500_000);
        await using var server = await TestFileServer.StartAsync(data);
        server.SupportRanges = false;
        server.SendLength = false;
        var info = NewInfo(server.Url);

        await new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None);

        Assert.Equal(data.Length, info.TotalSize);
        Assert.Equal(Hash(data), Hash(info.FilePath));
    }

    [Fact]
    public async Task DroppedConnections_AreRetried()
    {
        var data = TestFileServer.RandomBytes(4 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        server.AbortRequests = 3;
        var info = NewInfo(server.Url, connections: 4);

        await new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(info.FilePath));
    }

    [Fact]
    public async Task PauseThenResumeFromDatabase_ProducesIdenticalFile()
    {
        var data = TestFileServer.RandomBytes(8 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        var dbPath = Path.Combine(_dir, "test.db");
        Directory.CreateDirectory(_dir);

        // First run: capped at 2 MB/s, paused after ~1s.
        var info = NewInfo(server.Url);
        using (var store = new DownloadStore(dbPath))
        {
            store.Add(info);
            var task = new DownloadTask(info, Http, FastOptions, new RateLimiter(2 * 1024 * 1024));
            task.Checkpoint += store.Save;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.RunAsync(cts.Token));
        }

        // "Restart the app": reload from SQLite.
        using var store2 = new DownloadStore(dbPath);
        var reloaded = Assert.Single(store2.LoadAll());
        Assert.Equal(DownloadStatus.Paused, reloaded.Status);
        Assert.InRange(reloaded.Downloaded, 1, data.Length - 1);
        var savedPositions = reloaded.Segments.Where(s => s.Position <= s.End).Select(s => s.Position).ToHashSet();

        while (server.RangeStarts.TryDequeue(out _)) { }
        var resume = new DownloadTask(reloaded, Http, FastOptions);
        resume.Checkpoint += store2.Save;
        await resume.RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, reloaded.Status);
        Assert.Equal(Hash(data), Hash(reloaded.FilePath));
        // Every resumed connection picked up where a saved segment left off (or split one); nothing restarted from 0.
        Assert.Contains(server.RangeStarts, s => savedPositions.Contains(s));
        Assert.DoesNotContain(0L, server.RangeStarts);
        Assert.Equal(DownloadStatus.Completed, Assert.Single(store2.LoadAll()).Status);
    }

    [Fact]
    public async Task ChangedETagOnResume_IsDetected()
    {
        var data = TestFileServer.RandomBytes(8 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        var info = NewInfo(server.Url);

        var task = new DownloadTask(info, Http, FastOptions, new RateLimiter(2 * 1024 * 1024));
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.RunAsync(cts.Token));

        server.ETag = "\"v2\"";
        await Assert.ThrowsAsync<RemoteFileChangedException>(
            () => new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None));
        Assert.Equal(DownloadStatus.Failed, info.Status);
    }

    [Fact]
    public async Task NotFound_FailsWithoutRetrying()
    {
        await using var server = await TestFileServer.StartAsync([1, 2, 3]);
        var info = NewInfo(server.Url.Replace("/file", "/missing"));

        var ex = await Assert.ThrowsAsync<DownloadFailedException>(
            () => new DownloadTask(info, Http, FastOptions).RunAsync(CancellationToken.None));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task ServerLimitingConnections_FallsBackToFewerInsteadOfStalling()
    {
        // Like ash-speed.hetzner.com: 2 connections per client, 429 for the rest.
        var data = TestFileServer.RandomBytes(6 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        server.MaxConcurrent = 2;
        server.ThrottleBytesPerSecond = 8 * 1024 * 1024; // slow enough that requests overlap
        var info = NewInfo(server.Url, connections: 8);
        var log = new List<string>();

        var task = new DownloadTask(info, Http, FastOptions with { RetryBaseDelay = TimeSpan.FromSeconds(1) });
        task.Log += m => { lock (log) log.Add(m); };
        var sw = Stopwatch.StartNew();
        await task.RunAsync(CancellationToken.None);

        Assert.Equal(Hash(data), Hash(info.FilePath));
        Assert.Contains(log, m => m.Contains("limiting connections"));
        // 6 MB over 2 connections at 8 MB/s each ≈ 0.4 s; the old retry-with-backoff behaviour took many seconds.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task StalledConnection_IsTakenOverInsteadOfWaitingForTimeout()
    {
        // 4 parts of 512 KB; one goes silent after 100 KB. What's left (~412 KB) is too small to split,
        // so only a takeover finishes it before the 30 s read timeout.
        var data = TestFileServer.RandomBytes(2 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        server.StallRequests = 1;
        var info = NewInfo(server.Url, connections: 4);
        var options = FastOptions with { ReadTimeout = TimeSpan.FromSeconds(30), StealAfter = TimeSpan.FromSeconds(1) };
        var log = new List<string>();

        var task = new DownloadTask(info, Http, options);
        task.Log += m => { lock (log) log.Add(m); };
        var sw = Stopwatch.StartNew();
        await task.RunAsync(CancellationToken.None);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
        Assert.Contains(log, m => m.Contains("took it over"));
        Assert.Equal(Hash(data), Hash(info.FilePath));
    }

    [Fact]
    public async Task UnreachableServer_RetriesThenFails()
    {
        // Grab a free port, then close it so nothing is listening there.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var info = NewInfo($"http://127.0.0.1:{port}/file");
        var options = FastOptions with { MaxRetries = 1, RetryMaxDelay = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<DownloadFailedException>(
            () => new DownloadTask(info, Http, options).RunAsync(CancellationToken.None));
        Assert.Equal(DownloadStatus.Failed, info.Status);
        Assert.False(string.IsNullOrEmpty(info.Error));
    }

    [Fact]
    public async Task FileName_ComesFromContentDisposition_AndAvoidsClashes()
    {
        var data = TestFileServer.RandomBytes(10_000);
        await using var server = await TestFileServer.StartAsync(data);
        server.ContentDisposition = "attachment; filename=\"report final.pdf\"";

        var a = NewInfo(server.Url);
        await new DownloadTask(a, Http, FastOptions).RunAsync(CancellationToken.None);
        var b = NewInfo(server.Url);
        await new DownloadTask(b, Http, FastOptions).RunAsync(CancellationToken.None);

        Assert.Equal("report final.pdf", a.FileName);
        Assert.Equal("report final (1).pdf", b.FileName);
    }

    [Fact]
    public async Task GlobalSpeedLimit_IsRespected()
    {
        const int size = 3 * 1024 * 1024;
        var data = TestFileServer.RandomBytes(size);
        await using var server = await TestFileServer.StartAsync(data);
        var info = NewInfo(server.Url, connections: 4);

        var sw = Stopwatch.StartNew();
        await new DownloadTask(info, Http, FastOptions, new RateLimiter(1024 * 1024)).RunAsync(CancellationToken.None);
        sw.Stop();

        // 3 MB at 1 MB/s ≈ 3 s, minus the small initial burst allowance.
        Assert.InRange(sw.Elapsed.TotalSeconds, 2.5, 4.5);
        Assert.Equal(Hash(data), Hash(info.FilePath));
    }
}
