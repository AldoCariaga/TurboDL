using TurboDL.Core.Model;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

/// <summary>The states a video download goes through, driven by the fake yt-dlp in a folder of its own.</summary>
public sealed class VideoJobTests : IDisposable
{
    private readonly FakeToolsFolder _f = new();

    public void Dispose() => _f.Dispose();

    private DownloadInfo Info(string scenario, out string log, long? speedLimit = null, VideoQuality quality = VideoQuality.Max1080)
    {
        (var url, log) = _f.Url(scenario);
        return new DownloadInfo
        {
            Url = url,
            Kind = DownloadKind.Video,
            FileName = "Fäke vidéo ✓.mp4",
            Directory = _f.Output,
            Category = "Video",
            SpeedLimit = speedLimit,
            Headers = new Dictionary<string, string> { ["Cookie"] = "sid=s3cret" },
            Video = new VideoOptions { Quality = quality, OutputName = "Fäke vidéo ✓", Title = "Fäke vidéo ✓ 動画" },
        };
    }

    [Fact]
    public async Task Completed_TakesTheRealFileName_AndClearsLeftovers()
    {
        var info = Info("video", out var log);
        var job = new VideoJob(info, _f.Service());
        var saves = 0;
        job.Checkpoint += _ => Interlocked.Increment(ref saves);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, info.Status);
        Assert.NotNull(info.CompletedUtc);
        Assert.Null(info.Error);
        Assert.Equal(_f.Output, info.Directory);
        Assert.Equal("Fäke vidéo ✓.mp4", info.FileName);
        Assert.Equal(new FileInfo(info.FilePath).Length, info.TotalSize);
        Assert.True(info.Video!.Started);
        Assert.Empty(info.Video.PartialFiles);
        Assert.Null(info.Video.PhaseText);
        Assert.Empty(VideoJob.LeftoverFiles(info));
        Assert.True(saves >= 2, $"saved {saves} times");

        var run = FakeToolsFolder.ReadLog(log);
        Assert.DoesNotContain("--continue", run.Args);
        Assert.Contains("res:1080,ext:mp4:m4a", run.Args);
        // The name chosen in the dialog, not yt-dlp's title template.
        Assert.Contains("Fäke vidéo ✓.%(ext)s", run.Args);
        Assert.DoesNotContain(run.Args, a => a.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paused_KeepsWhatToCleanUp_AndTheNextRunResumes()
    {
        var info = Info("slow", out var log);
        var job = new VideoJob(info, _f.Service());
        using var cts = new CancellationTokenSource();
        var run = job.RunAsync(cts.Token);

        for (var i = 0; i < 150 && job.GetSnapshot().Downloaded == 0; i++) await Task.Delay(100);
        var snap = job.GetSnapshot();
        Assert.Equal(DownloadStatus.Downloading, info.Status);
        Assert.Equal(VideoPhase.Downloading, snap.Phase);
        Assert.Equal(10_000_000, snap.Total);
        Assert.Equal(1000, snap.BytesPerSecond);
        Assert.Equal("Downloading", snap.PhaseText);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(DownloadStatus.Paused, info.Status);
        Assert.True(info.Video!.Started);
        Assert.True(info.Video.DownloadedBytes > 0);
        Assert.Contains(info.Video.PartialFiles, f => f.EndsWith(".f18.mp4.part", StringComparison.Ordinal));
        var leftovers = VideoJob.LeftoverFiles(info).ToList();
        Assert.Contains(leftovers, f => f.EndsWith(".f18.mp4", StringComparison.Ordinal));
        Assert.Contains(leftovers, f => f.EndsWith(".f18.mp4.part", StringComparison.Ordinal));
        Assert.Contains(leftovers, f => f.EndsWith(".f18.mp4.ytdl", StringComparison.Ordinal));

        // Resuming is just another run: yt-dlp is asked to carry on from its .part files.
        var (url, log2) = _f.Url("video");
        info.Url = url;
        await new VideoJob(info, _f.Service()).RunAsync(CancellationToken.None);
        Assert.Contains("--continue", FakeToolsFolder.ReadLog(log2).Args);
        Assert.Equal(DownloadStatus.Completed, info.Status);
        Assert.DoesNotContain("--continue", FakeToolsFolder.ReadLog(log).Args);
    }

    [Fact]
    public async Task Failed_KeepsYtDlpsMessage()
    {
        (var url, _) = _f.Url("fail", "example.com");
        var info = Info("fail", out _);
        info.Url = url;
        var job = new VideoJob(info, _f.Service());

        await Assert.ThrowsAsync<VideoDownloadException>(() => job.RunAsync(CancellationToken.None));
        Assert.Equal(DownloadStatus.Failed, info.Status);
        Assert.Equal("ERROR: [generic] Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)", info.Error);
        Assert.Null(info.Video!.PhaseText);

        // A later successful run clears the error.
        (url, _) = _f.Url("video");
        info.Url = url;
        await new VideoJob(info, _f.Service()).RunAsync(CancellationToken.None);
        Assert.Null(info.Error);
        Assert.Equal(DownloadStatus.Completed, info.Status);
    }

    [Fact]
    public async Task Snapshot_AddsUpTheStreams()
    {
        var info = Info("video", out _);
        var job = new VideoJob(info, _f.Service());
        await job.RunAsync(CancellationToken.None);

        // 3000 bytes of video + an estimated 1000 of audio; after the run, the real file size.
        var snap = job.GetSnapshot();
        Assert.Equal(4000, snap.Downloaded);
        Assert.Equal(4000, snap.Total);
        Assert.True(snap.TotalIsEstimate);
        Assert.Equal(VideoPhase.Merging, snap.Phase);
        Assert.Equal(0, snap.BytesPerSecond); // not downloading any more
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(500, 0, 500)]
    [InlineData(0, 800, 800)]
    [InlineData(500, 800, 500)]
    [InlineData(900, 800, 800)]
    [InlineData(-1, 800, 800)]
    public void SpeedLimit_IsTheTighterOfTheTwo(long perDownload, long global, long expected) =>
        Assert.Equal(expected, VideoJob.EffectiveLimit(perDownload, global));

    [Fact]
    public async Task SpeedLimit_ReachesYtDlp()
    {
        var info = Info("video", out var log, speedLimit: 2_000_000);
        var job = new VideoJob(info, _f.Service(), globalLimit: 1_000_000);
        Assert.Equal(1_000_000, job.RateLimit);
        await job.RunAsync(CancellationToken.None);
        var args = FakeToolsFolder.ReadLog(log).Args;
        Assert.Equal("1000000", args[Array.IndexOf(args, "--limit-rate") + 1]);
    }

    [Fact]
    public void NotAVideo_IsRefused()
    {
        var info = new DownloadInfo { Url = "https://e.example/f.zip" };
        Assert.Throws<ArgumentException>(() => new VideoJob(info, _f.Service()));
    }

    [Fact]
    public void Options_DescribeThemselves()
    {
        Assert.Equal("Best quality · mp4", new VideoOptions().Describe());
        Assert.Equal("Up to 720p · mkv", new VideoOptions { Quality = VideoQuality.Max720, Container = VideoContainer.Mkv }.Describe());
        Assert.Equal("Audio only", new VideoOptions { Quality = VideoQuality.AudioOnly }.Describe());
        Assert.Equal("Format 137 + ba · mp4", new VideoOptions { FormatIds = ["137", "ba"] }.Describe());
    }

    [Fact]
    public void Options_SurviveTheStore()
    {
        var path = Path.Combine(_f.Root, "store.db");
        long id;
        using (var store = new Storage.DownloadStore(path))
        {
            var info = Info("video", out _);
            info.Video!.Started = true;
            info.Video.PartialFiles.Add(@"C:\x\a.f137.mp4.part");
            info.Video.FormatIds = ["137", "ba"];
            info.Video.DownloadedBytes = 1234;
            store.Add(info);
            id = info.Id;
        }
        using (var store = new Storage.DownloadStore(path))
        {
            var loaded = store.LoadAll().Single(i => i.Id == id);
            Assert.Equal(DownloadKind.Video, loaded.Kind);
            Assert.True(loaded.Video!.Started);
            Assert.Equal(VideoQuality.Max1080, loaded.Video.Quality);
            Assert.Equal(["137", "ba"], loaded.Video.FormatIds);
            Assert.Equal([@"C:\x\a.f137.mp4.part"], loaded.Video.PartialFiles);
            Assert.Equal(1234, loaded.Downloaded);
            Assert.Equal("Fäke vidéo ✓ 動画", loaded.Video.Title);
        }
    }
}
