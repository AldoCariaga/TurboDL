using System.Diagnostics;
using System.Security.Cryptography;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;

namespace TurboDL.Core.Tests;

public sealed class HostThrottleTests : IDisposable
{
    private const string HostA = "https://a.example/file1";
    private const string HostA2 = "https://a.example/file2";
    private const string HostB = "https://b.example/file";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TurboDL.HostThrottleTests." + Guid.NewGuid().ToString("N"));
    private static readonly HttpClient Http = TurboHttp.CreateClient();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static async Task<TimeSpan> Time(Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        return watch.Elapsed;
    }

    [Fact]
    public async Task StartsOnOneHost_GoOneAfterAnother()
    {
        var throttle = new HostThrottle { StartInterval = TimeSpan.FromMilliseconds(150) };
        var watch = Stopwatch.StartNew();
        var done = new List<TimeSpan>();

        await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            await throttle.WaitToStartAsync(i % 2 == 0 ? HostA : HostA2, CancellationToken.None);
            lock (done) done.Add(watch.Elapsed);
        }));

        done.Sort();
        Assert.True(done[0] < TimeSpan.FromMilliseconds(100), $"first waited {done[0]}");
        Assert.True(done[2] >= TimeSpan.FromMilliseconds(280), $"third started at {done[2]}");
    }

    [Fact]
    public async Task OtherHosts_DontWaitForEachOther()
    {
        var throttle = new HostThrottle { StartInterval = TimeSpan.FromSeconds(5) };
        await throttle.WaitToStartAsync(HostA, CancellationToken.None);

        var elapsed = await Time(() => throttle.WaitToStartAsync(HostB, CancellationToken.None));

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"waited {elapsed}");
    }

    [Fact]
    public async Task Backoff_HoldsEveryRequestToTheHost_ForRetryAfter()
    {
        var throttle = new HostThrottle();
        Assert.Equal(TimeSpan.FromMilliseconds(400), throttle.Backoff(HostA, TimeSpan.FromMilliseconds(400)));

        var held = await Time(() => throttle.WaitIfBackingOffAsync(HostA2, CancellationToken.None));
        var other = await Time(() => throttle.WaitIfBackingOffAsync(HostB, CancellationToken.None));

        Assert.True(held >= TimeSpan.FromMilliseconds(350), $"held {held}");
        Assert.True(other < TimeSpan.FromMilliseconds(100), $"other host held {other}");
    }

    [Fact]
    public async Task BurstOf429s_CountsOnce_RepeatsDouble_AndSuccessResets()
    {
        var throttle = new HostThrottle { FirstBackoff = TimeSpan.FromMilliseconds(100) };

        Assert.Equal(TimeSpan.FromMilliseconds(100), throttle.Backoff(HostA, null));
        Assert.True(throttle.Backoff(HostA, null) <= TimeSpan.FromMilliseconds(100)); // same burst: no longer
        await Task.Delay(150);
        Assert.Equal(TimeSpan.FromMilliseconds(200), throttle.Backoff(HostA, null));
        await Task.Delay(250);
        throttle.Succeeded(HostA);
        Assert.Equal(TimeSpan.FromMilliseconds(100), throttle.Backoff(HostA, null));
    }

    [Fact]
    public void RetryAfter_IsCapped()
    {
        var throttle = new HostThrottle { MaxBackoff = TimeSpan.FromSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(30), throttle.Backoff(HostA, TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task WaitIsCancellable()
    {
        var throttle = new HostThrottle();
        throttle.Backoff(HostA, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.WaitIfBackingOffAsync(HostA, cts.Token));
    }

    [Fact]
    public async Task ManyDownloads_OneHostSays429_AllWaitRetryAfter_ThenAllFinish()
    {
        var data = RandomNumberGenerator.GetBytes(200_000);
        await using var server = await TestFileServer.StartAsync(data);
        server.BusyRequests = 1;
        server.BusyRetryAfterSeconds = 1;
        // Starts far enough apart that the 429 is back before the second download asks, even on a busy test machine.
        var throttle = new HostThrottle { StartInterval = TimeSpan.FromMilliseconds(400) };
        var options = new DownloadOptions
        {
            RetryBaseDelay = TimeSpan.FromMilliseconds(20),
            RetryMaxDelay = TimeSpan.FromMilliseconds(50),
            MinSegmentSize = 64 * 1024,
        };
        var infos = Enumerable.Range(0, 3).Select(i => new DownloadInfo
        {
            Url = server.Url,
            Directory = _dir,
            FileName = $"part{i}.bin",
            Connections = 2,
        }).ToList();

        await Task.WhenAll(infos.Select(info => new DownloadTask(info, Http, options, hostThrottle: throttle).RunAsync(CancellationToken.None)));

        Assert.All(infos, info =>
        {
            Assert.Equal(DownloadStatus.Completed, info.Status);
            Assert.Equal(data, File.ReadAllBytes(info.FilePath));
        });
        var busyAt = Assert.Single(server.BusyTimes);
        // Nothing reached the host while it had asked for a second's rest (allowing for requests already on the wire).
        var during = server.RequestTimes.Count(t => t > busyAt.AddMilliseconds(100) && t < busyAt.AddMilliseconds(900));
        Assert.Equal(0, during);
    }
}
