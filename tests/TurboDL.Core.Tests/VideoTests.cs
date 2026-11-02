using System.Diagnostics;
using System.Text.Json;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

/// <summary>A tools folder holding the fake yt-dlp (tests/FakeYtDlp) plus placeholder ffmpeg, ffprobe and deno.</summary>
public sealed class FakeToolsFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TurboDL.VideoTests." + Guid.NewGuid().ToString("N"));
    public string Tools => Path.Combine(Root, "tools");
    public string Output => Path.Combine(Root, "out");

    public FakeToolsFolder(bool withYtDlp = true)
    {
        Directory.CreateDirectory(Tools);
        if (withYtDlp)
        {
            var bin = AppContext.BaseDirectory;
            foreach (var f in new[] { "FakeYtDlp.dll", "FakeYtDlp.runtimeconfig.json", "FakeYtDlp.deps.json" })
                File.Copy(Path.Combine(bin, f), Path.Combine(Tools, f));
            // The apphost finds FakeYtDlp.dll by its embedded name, so it can be renamed.
            File.Copy(Path.Combine(bin, "FakeYtDlp.exe"), Path.Combine(Tools, "yt-dlp.exe"));
        }
        foreach (var f in new[] { "ffmpeg.exe", "ffprobe.exe", "deno.exe" })
            File.WriteAllText(Path.Combine(Tools, f), "placeholder");
    }

    public YtDlpService Service(TimeSpan? probeTimeout = null) =>
        new(new YtDlpTools(Tools)) { ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(15) };

    /// <summary>A URL for the fake; it records its arguments to the returned log file.</summary>
    public (string Url, string Log) Url(string scenario, string host = "www.youtube.com")
    {
        var log = Path.Combine(Root, $"log-{Guid.NewGuid():N}.json");
        return ($"https://{host}/{scenario}?log={Uri.EscapeDataString(log)}", log);
    }

    public static FakeRun ReadLog(string log) => JsonSerializer.Deserialize<FakeRun>(File.ReadAllText(log),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeRun
{
    public string[] Args { get; set; } = [];
    public string? CookiesPath { get; set; }
    public string? Cookies { get; set; }
    public int Pid { get; set; }
    public int? ChildPid { get; set; }
}

/// <summary>Everything that runs the fake yt-dlp. One class, so these tests don't run in parallel with each other.</summary>
public sealed class YtDlpServiceTests : IDisposable
{
    private readonly FakeToolsFolder _f = new();

    public void Dispose() => _f.Dispose();

    private static bool IsGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<bool> GoneSoonAsync(int pid)
    {
        for (var i = 0; i < 50 && !IsGone(pid); i++) await Task.Delay(100);
        return IsGone(pid);
    }

    [Fact]
    public async Task Probe_ParsesFormats_AndKeepsUnicodeTitle()
    {
        var (url, _) = _f.Url("video");
        var result = await _f.Service().ProbeAsync(url);

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        var info = result.Info!;
        Assert.Equal("abc123", info.Id);
        Assert.Equal("Fäke vidéo ✓ 動画", info.Title);
        Assert.Equal("youtube", info.Extractor);
        Assert.Equal(TimeSpan.FromSeconds(212.5), info.Duration);
        Assert.Equal("https://i.example/abc123.jpg", info.Thumbnail);
        // The storyboard is dropped; the format with unknown codecs is kept.
        Assert.Equal(["140", "18", "137", "hls-x"], info.Formats.Select(x => x.Id));

        var audio = info.Formats[0];
        Assert.True(audio.IsAudioOnly);
        Assert.False(audio.IsVideoOnly);
        Assert.Equal(3_400_000, audio.Size);

        var combined = info.Formats[1];
        Assert.False(combined.IsAudioOnly || combined.IsVideoOnly);
        Assert.Equal(360, combined.Height);
        Assert.Null(combined.FileSize);
        Assert.Equal(9_000_000, combined.Size);

        var video = info.Formats[2];
        Assert.True(video.IsVideoOnly);
        Assert.Equal(1920, video.Width);
        Assert.Equal(29.97, video.Fps);
        Assert.Equal("1080p", video.Note);

        var unknown = info.Formats[3];
        Assert.Null(unknown.VCodec);
        Assert.False(unknown.IsAudioOnly || unknown.IsVideoOnly);
        Assert.Null(unknown.Height);
    }

    [Fact]
    public async Task Probe_UnsupportedUrl_IsNull()
    {
        var (url, _) = _f.Url("unsupported", "example.com");
        Assert.Null(await _f.Service().ProbeAsync(url));
    }

    [Fact]
    public async Task Probe_RealError_IsTyped_WithYtDlpsMessageAsIs()
    {
        var (url, _) = _f.Url("geo");
        var result = await _f.Service().ProbeAsync(url);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(YtDlpErrorKind.GeoRestricted, result.Error!.Kind);
        // Only the ERROR line, unchanged; the WARNING before it isn't part of the message.
        Assert.Equal("ERROR: [youtube] abc123: The uploader has not made this video available in your country", result.Error.Message);
        Assert.Equal(1, result.Error.ExitCode);
    }

    [Fact]
    public async Task Probe_Playlist_IsTypedPlaylistError()
    {
        var (url, _) = _f.Url("playlist");
        var result = await _f.Service().ProbeAsync(url);

        Assert.NotNull(result);
        Assert.Equal(YtDlpErrorKind.Playlist, result.Error!.Kind);
        Assert.Equal("This link is a playlist; only single videos are supported for now.", result.Error.Message);
    }

    [Fact]
    public async Task Probe_Timeout_IsTyped_AndKillsTheProcessTree()
    {
        var (url, log) = _f.Url("hang");
        var sw = Stopwatch.StartNew();
        var result = await _f.Service(TimeSpan.FromSeconds(2)).ProbeAsync(url);
        sw.Stop();

        Assert.NotNull(result);
        Assert.Equal(YtDlpErrorKind.Timeout, result.Error!.Kind);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        var run = FakeToolsFolder.ReadLog(log);
        Assert.True(await GoneSoonAsync(run.Pid), "yt-dlp still running");
        Assert.True(await GoneSoonAsync(run.ChildPid!.Value), "its child still running");
    }

    [Fact]
    public async Task Probe_PassesBrowserHeaders_CookiesOnlyThroughATempFile()
    {
        var (url, log) = _f.Url("video");
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = "SID=secret-one; HSID=secret-two",
            ["Referer"] = "https://www.youtube.com/",
            ["User-Agent"] = "Mozilla/5.0 Test",
        };
        var result = await _f.Service().ProbeAsync(url, headers);
        Assert.True(result!.IsSuccess);

        var run = FakeToolsFolder.ReadLog(log);
        Assert.DoesNotContain(run.Args, a => a.Contains("secret", StringComparison.Ordinal));
        Assert.Contains("--referer", run.Args);
        Assert.Contains("Mozilla/5.0 Test", run.Args);
        Assert.NotNull(run.CookiesPath);
        Assert.Contains(".youtube.com\tTRUE\t/\tFALSE\t", run.Cookies);
        Assert.Contains("\tSID\tsecret-one\n", run.Cookies);
        Assert.Contains("\tHSID\tsecret-two\n", run.Cookies);
        Assert.False(File.Exists(run.CookiesPath), "cookies file left behind");
        Assert.Contains("--js-runtimes", run.Args);
        Assert.Contains("deno:" + Path.Combine(_f.Tools, "deno.exe"), run.Args);
    }

    [Fact]
    public async Task Probe_WithoutYtDlp_IsToolsMissing()
    {
        using var empty = new FakeToolsFolder(withYtDlp: false);
        var result = await empty.Service().ProbeAsync("https://www.youtube.com/watch?v=x");
        Assert.Equal(YtDlpErrorKind.ToolsMissing, result!.Error!.Kind);
    }

    [Fact]
    public async Task Download_ReportsPhases_AndReturnsTheFinalPath()
    {
        var (url, log) = _f.Url("video");
        var reports = new List<VideoProgress>();
        var progress = new SyncProgress<VideoProgress>(reports.Add);

        var result = await _f.Service().DownloadAsync(new VideoDownloadRequest { Url = url, OutputDirectory = _f.Output }, progress);

        Assert.Equal(Path.Combine(_f.Output, "Fäke vidéo ✓.mp4"), result.FilePath);
        Assert.True(File.Exists(result.FilePath));

        var video = reports.Where(r => r.Phase == VideoPhase.DownloadingVideo).ToList();
        var audio = reports.Where(r => r.Phase == VideoPhase.DownloadingAudio).ToList();
        Assert.Equal(3, video.Count);
        Assert.Equal([0L, 1500, 3000], video.Select(v => v.DownloadedBytes));
        Assert.All(video, v => Assert.Equal(3000, v.TotalBytes));
        Assert.All(video, v => Assert.False(v.IsEstimate));
        Assert.Equal("137", video[0].FormatId);
        Assert.Equal(1500, video[1].Speed);
        Assert.Equal(TimeSpan.FromSeconds(1), video[1].Eta);
        Assert.Equal(2, audio.Count);
        Assert.All(audio, a => Assert.True(a.IsEstimate));
        Assert.Equal(1000, audio[1].TotalBytes);
        Assert.Equal(VideoPhase.Merging, reports[^1].Phase);
        Assert.Equal("Merger", reports[^1].PostProcessor);

        var run = FakeToolsFolder.ReadLog(log);
        Assert.DoesNotContain("--continue", run.Args);
        Assert.Contains("--ffmpeg-location", run.Args);
        Assert.Contains(_f.Tools, run.Args);
    }

    [Fact]
    public async Task Download_Cancel_KillsTheWholeTree()
    {
        var (url, log) = _f.Url("slow");
        var firstProgress = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<VideoProgress>(_ => firstProgress.TrySetResult());

        var download = _f.Service().DownloadAsync(new VideoDownloadRequest { Url = url, OutputDirectory = _f.Output }, progress, cts.Token);
        await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var run = FakeToolsFolder.ReadLog(log);
        Assert.False(IsGone(run.Pid));
        Assert.False(IsGone(run.ChildPid!.Value));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(await GoneSoonAsync(run.Pid), "yt-dlp still running");
        Assert.True(await GoneSoonAsync(run.ChildPid.Value), "its child (ffmpeg's stand-in) still running");
    }

    [Fact]
    public async Task Download_Resume_AddsContinue()
    {
        var (url, log) = _f.Url("video");
        await _f.Service().DownloadAsync(new VideoDownloadRequest { Url = url, OutputDirectory = _f.Output, Resume = true });
        Assert.Contains("--continue", FakeToolsFolder.ReadLog(log).Args);
    }

    [Fact]
    public async Task Download_Cookies_GoThroughATempFile_ThatIsDeletedAfterwards()
    {
        var (url, log) = _f.Url("video");
        var headers = new Dictionary<string, string> { ["cookie"] = "token=hunter2-value", ["X-Extra"] = "1" };
        await _f.Service().DownloadAsync(new VideoDownloadRequest { Url = url, OutputDirectory = _f.Output, Headers = headers, RateLimit = 500_000 });

        var run = FakeToolsFolder.ReadLog(log);
        Assert.DoesNotContain(run.Args, a => a.Contains("hunter2", StringComparison.Ordinal));
        Assert.Contains("\ttoken\thunter2-value\n", run.Cookies);
        Assert.False(File.Exists(run.CookiesPath));
        Assert.Contains("X-Extra:1", run.Args);
        var i = Array.IndexOf(run.Args, "--limit-rate");
        Assert.Equal("500000", run.Args[i + 1]);
    }

    [Fact]
    public async Task Download_Failure_ThrowsWithYtDlpsMessageAsIs()
    {
        var (url, _) = _f.Url("fail", "example.com");
        var ex = await Assert.ThrowsAsync<VideoDownloadException>(() =>
            _f.Service().DownloadAsync(new VideoDownloadRequest { Url = url, OutputDirectory = _f.Output }));
        Assert.Equal(YtDlpErrorKind.Unavailable, ex.Error.Kind);
        Assert.Equal("ERROR: [generic] Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)", ex.Message);
    }

    [Fact]
    public async Task Download_WithoutTools_Throws_ToolsMissing()
    {
        using var empty = new FakeToolsFolder(withYtDlp: false);
        var ex = await Assert.ThrowsAsync<VideoDownloadException>(() =>
            empty.Service().DownloadAsync(new VideoDownloadRequest { Url = "https://x.example/v", OutputDirectory = empty.Output }));
        Assert.Equal(YtDlpErrorKind.ToolsMissing, ex.Error.Kind);
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, so the order is exact.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this) report(value);
        }
    }
}

public sealed class YtDlpArgumentsTests
{
    private static List<string> Download(VideoQuality quality = VideoQuality.Best, VideoContainer container = VideoContainer.Mp4,
        IReadOnlyList<string>? ids = null, string? fileName = null) =>
        YtDlpArguments.Download(new VideoDownloadRequest
        {
            Url = "https://www.youtube.com/watch?v=x",
            OutputDirectory = @"C:\out",
            Quality = quality,
            Container = container,
            FormatIds = ids,
            FileName = fileName,
        }, @"C:\tools", null, null);

    private static string? After(List<string> args, string option)
    {
        var i = args.IndexOf(option);
        return i >= 0 ? args[i + 1] : null;
    }

    [Fact]
    public void Presets_MapToFormatAndSort()
    {
        var best = Download();
        Assert.Equal("bv*+ba/b", After(best, "-f"));
        Assert.Equal("vcodec:h264,res,acodec:aac,ext:mp4:m4a", After(best, "-S"));
        Assert.Equal("mp4", After(best, "--merge-output-format"));
        Assert.Equal("mp4", After(best, "--remux-video"));

        Assert.Equal("res:1080,vcodec:h264,acodec:aac,ext:mp4:m4a", After(Download(VideoQuality.Max1080), "-S"));
        Assert.Equal("res:720,vcodec:h264,acodec:aac,ext:mp4:m4a", After(Download(VideoQuality.Max720), "-S"));
        Assert.Equal("res:720", After(Download(VideoQuality.Max720, VideoContainer.Mkv), "-S"));
        Assert.Null(After(Download(VideoQuality.Best, VideoContainer.Mkv), "-S"));
        Assert.Equal("mkv", After(Download(VideoQuality.Best, VideoContainer.Mkv), "--merge-output-format"));

        var audio = Download(VideoQuality.AudioOnly);
        Assert.Equal("ba/b", After(audio, "-f"));
        Assert.Contains("-x", audio);
        Assert.DoesNotContain("--merge-output-format", audio);
        Assert.Equal("acodec:aac,ext:mp4:m4a", After(audio, "-S"));
        Assert.Null(After(Download(VideoQuality.AudioOnly, VideoContainer.Mkv), "-S"));
    }

    [Fact]
    public void Mp4_PrefersH264_WithoutGivingUpResolution()
    {
        // Picked on a YouTube test video by yt-dlp 2026.10.26 (--simulate): 1080 → 299 avc1 (was 399 av01), and with every
        // H.264 format removed, 1080 → 617 vp9 at 1080p, i.e. the capped presets never drop the height.
        foreach (var q in new[] { VideoQuality.Max1080, VideoQuality.Max720 })
        {
            var sort = After(Download(q), "-S")!.Split(',');
            Assert.StartsWith("res", sort[0]);
            Assert.Equal(["vcodec:h264", "acodec:aac", "ext:mp4:m4a"], sort[1..]);
        }
        Assert.Equal("res:720", After(Download(VideoQuality.Max720, VideoContainer.Mkv), "-S"));
    }

    [Fact]
    public void BestMp4_PutsH264First_MkvKeepsTheBestCodec()
    {
        // On the same test video: → 299+140 (H.264 1080p60 + AAC) instead of 623 (VP9 1440p).
        Assert.Equal("vcodec:h264,res,acodec:aac,ext:mp4:m4a", After(Download(VideoQuality.Best), "-S"));
        Assert.Null(After(Download(VideoQuality.Best, VideoContainer.Mkv), "-S"));
    }

    [Fact]
    public void ExplicitFormatIds_WinOverThePreset()
    {
        var args = Download(VideoQuality.Max720, VideoContainer.Mkv, ids: ["137", "ba"]);
        Assert.Equal("137+ba", After(args, "-f"));
        Assert.Null(After(args, "-S"));
        Assert.Equal("mkv", After(args, "--merge-output-format"));
        Assert.DoesNotContain("-x", args);

        // A hand-picked audio format stays audio.
        var audio = Download(VideoQuality.AudioOnly, ids: ["140"]);
        Assert.Equal("140", After(audio, "-f"));
        Assert.Contains("-x", audio);
        Assert.DoesNotContain("--remux-video", audio);
    }

    [Fact]
    public void Templates_ProgressAndFinalPath()
    {
        var args = Download();
        Assert.Equal(YtDlpArguments.TitleTemplate, After(args, "-o"));
        Assert.Equal(@"C:\out", After(args, "-P"));
        Assert.Contains("--newline", args);
        Assert.Contains("--progress", args);
        Assert.Contains(YtDlpArguments.ProgressTemplate, args);
        Assert.Contains(YtDlpArguments.PostProcessTemplate, args);
        Assert.Equal(YtDlpArguments.FilePrintTemplate, After(args, "--print"));
        Assert.Contains("--ignore-config", args);
        Assert.Contains("--no-playlist", args);
        Assert.Equal("utf-8", After(args, "--encoding"));
    }

    [Fact]
    public void ChosenFileName_IsLiteral()
    {
        Assert.Equal("100%% done_.%(ext)s", After(Download(fileName: "100% done?"), "-o"));
    }

    [Fact]
    public void Url_ComesLast_AfterDoubleDash()
    {
        var args = YtDlpArguments.Probe("--exec=calc", null, null, null, allowGenericExtractor: true);
        Assert.Equal("--", args[^2]);
        Assert.Equal("--exec=calc", args[^1]);
        Assert.Contains("-J", args);
        Assert.DoesNotContain("--ies", args);
    }

    [Fact]
    public void GenericExtractor_CanBeTurnedOff()
    {
        var args = YtDlpArguments.Probe("https://e.example/", null, null, @"C:\t\deno.exe", allowGenericExtractor: false);
        Assert.Equal("default,-generic", After(args, "--ies"));
        Assert.Equal(@"deno:C:\t\deno.exe", After(args, "--js-runtimes"));
    }

    [Fact]
    public void CredentialHeaders_NeverReachTheCommandLine()
    {
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = "a=secret",
            ["Authorization"] = "Bearer secret",
            ["Range"] = "bytes=0-",
            ["Accept-Language"] = "en",
        };
        var args = YtDlpArguments.Probe("https://e.example/", headers, @"C:\t\c.txt", null, true);
        Assert.DoesNotContain(args, a => a.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("Range", StringComparison.Ordinal));
        Assert.Equal("Accept-Language:en", After(args, "--add-headers"));
        Assert.Equal(@"C:\t\c.txt", After(args, "--cookies"));
    }
}

public sealed class YtDlpOutputTests
{
    [Fact]
    public void Progress_NaFields_AreNull()
    {
        // As real yt-dlp 2026.10.26 prints it: a missing %(...)j field is a bare NA, which isn't JSON.
        var p = YtDlpOutput.ParseProgress(YtDlpArguments.ProgressPrefix +
            """{"status":"downloading","downloaded":2048,"total":NA,"estimate":NA,"speed":NA,"eta":NA,"format":"18","vcodec":"avc1","acodec":"mp4a","file":"C:\\v\\NA,NA.mp4","tmp":NA}""");
        Assert.NotNull(p);
        Assert.Equal(VideoPhase.Downloading, p.Phase);
        Assert.Equal(2048, p.DownloadedBytes);
        Assert.Null(p.TotalBytes);
        Assert.False(p.IsEstimate);
        Assert.Null(p.Speed);
        Assert.Null(p.Eta);
        Assert.Null(p.TempFileName);
        Assert.Equal(@"C:\v\NA,NA.mp4", p.FileName); // an NA inside a string stays

        // A quoted "NA" is fine too.
        Assert.Null(YtDlpOutput.ParseProgress(YtDlpArguments.ProgressPrefix + """{"downloaded":1,"total":"NA"}""")!.TotalBytes);

        // The last line of a real stream download.
        var done = YtDlpOutput.ParseProgress(YtDlpArguments.ProgressPrefix +
            """{"status":"finished","downloaded":19661126,"total":19661126,"estimate":NA,"speed":31674069.8,"eta":NA,"format":"18","vcodec":"avc1.42001E","acodec":"mp4a.40.2","file":"C:\\raw.mp4","tmp":NA}""");
        Assert.Equal(19661126, done!.TotalBytes);
        Assert.Equal(31674069.8, done.Speed);
    }

    [Theory]
    // Text inside strings is never touched, even when it looks like ", NA," or ":NA}".
    [InlineData("""{"title":"Live, NA, 2024","f":":NA}","x":NA}""", """{"title":"Live, NA, 2024","f":":NA}","x":null}""")]
    // An escaped quote doesn't end the string; an escaped backslash right before the closing quote does.
    [InlineData("""{"t":"say \"NA\" now","u":NA}""", """{"t":"say \"NA\" now","u":null}""")]
    [InlineData("""{"p":"C:\\dir\\","q":NA,"r":"NA"}""", """{"p":"C:\\dir\\","q":null,"r":"NA"}""")]
    // Arrays, whitespace, several in a row.
    [InlineData("""[NA, 1, NA]""", """[null, 1, null]""")]
    [InlineData("""{"a": NA ,"b":NA}""", """{"a": null ,"b":null}""")]
    // Longer tokens aren't ours.
    [InlineData("""{"a":NAN,"b":NaN,"c":NA1}""", """{"a":NAN,"b":NaN,"c":NA1}""")]
    // Nothing to do: same string back.
    [InlineData("""{"a":1,"b":"x"}""", """{"a":1,"b":"x"}""")]
    public void FixNa_RewritesOnlyBareNaOutsideStrings(string input, string expected) =>
        Assert.Equal(expected, YtDlpOutput.FixNa(input));

    [Fact]
    public void Progress_TitleWithNa_KeepsTheFileName()
    {
        var p = YtDlpOutput.ParseProgress(YtDlpArguments.ProgressPrefix +
            """{"status":"downloading","downloaded":10,"total":NA,"estimate":100,"speed":NA,"eta":NA,"format":"137","vcodec":"avc1","acodec":"none","file":"C:\\v\\Live, NA, 2024.f137.mp4","tmp":"C:\\v\\Live, NA, 2024.f137.mp4.part"}""");
        Assert.Equal(@"C:\v\Live, NA, 2024.f137.mp4", p!.FileName);
        Assert.Equal(@"C:\v\Live, NA, 2024.f137.mp4.part", p.TempFileName);
        Assert.Equal(100, p.TotalBytes);
        Assert.True(p.IsEstimate);
        Assert.Equal(VideoPhase.DownloadingVideo, p.Phase);
    }

    [Fact]
    public void PostProcess_BareNa_IsTolerated() =>
        Assert.Equal(VideoPhase.Processing,
            YtDlpOutput.ParsePostProcess(YtDlpArguments.PostProcessPrefix + """{"status":"started","pp":NA}""")!.Phase);

    [Fact]
    public void OtherLines_AreIgnored()
    {
        Assert.Null(YtDlpOutput.ParseProgress("[download] Destination: x.mp4"));
        Assert.Null(YtDlpOutput.ParseProgress(YtDlpArguments.ProgressPrefix + "{broken"));
        Assert.Null(YtDlpOutput.ParsePostProcess(YtDlpArguments.PostProcessPrefix + """{"status":"finished","pp":"Merger"}"""));
        Assert.Null(YtDlpOutput.ParseFilePath(YtDlpArguments.FilePrefix + "NA"));
        Assert.Equal(@"C:\a b\c.mp4", YtDlpOutput.ParseFilePath(YtDlpArguments.FilePrefix + @"C:\a b\c.mp4"));
    }

    [Fact]
    public void PostProcess_MergerAndOthers()
    {
        Assert.Equal(VideoPhase.Merging,
            YtDlpOutput.ParsePostProcess(YtDlpArguments.PostProcessPrefix + """{"status":"started","pp":"Merger"}""")!.Phase);
        Assert.Equal(VideoPhase.Processing,
            YtDlpOutput.ParsePostProcess(YtDlpArguments.PostProcessPrefix + """{"status":"started","pp":"FFmpegVideoRemuxer"}""")!.Phase);
    }

    [Theory]
    [InlineData("ERROR: Unsupported URL: https://example.com/", YtDlpErrorKind.Unsupported)]
    [InlineData("ERROR: [Netflix] 123: This video is DRM protected", YtDlpErrorKind.Drm)]
    [InlineData("ERROR: [youtube] x: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies", YtDlpErrorKind.LoginRequired)]
    [InlineData("ERROR: [youtube] x: Private video. Sign in if you've been granted access to this video", YtDlpErrorKind.LoginRequired)]
    [InlineData("ERROR: [youtube] x: Video unavailable", YtDlpErrorKind.Unavailable)]
    [InlineData("ERROR: [generic] Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", YtDlpErrorKind.Network)]
    [InlineData("ERROR: [vimeo] 1: The uploader has not made this video available in your country", YtDlpErrorKind.GeoRestricted)]
    [InlineData("ERROR: something new", YtDlpErrorKind.Other)]
    public void Errors_AreClassified(string message, YtDlpErrorKind kind) =>
        Assert.Equal(kind, YtDlpOutput.Classify(message));

    [Fact]
    public void ErrorMessage_FallsBackToTheStderrTail()
    {
        Assert.Equal("boom", YtDlpOutput.ErrorMessage(["boom", YtDlpArguments.ProgressPrefix + "{}", YtDlpArguments.PostProcessPrefix + "{}"], 1));
        Assert.Equal("Traceback\nboom".Replace("\n", Environment.NewLine), YtDlpOutput.ErrorMessage(["", "Traceback", "boom"], 1));
        Assert.Equal("yt-dlp exited with code 3.", YtDlpOutput.ErrorMessage([], 3));
    }
}

public sealed class NetscapeCookiesTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", ".youtube.com")]
    [InlineData("https://m.youtube.com/watch?v=1", ".m.youtube.com")]
    [InlineData("https://vimeo.com/1", ".vimeo.com")]
    [InlineData("https://WWW.Example.COM/", ".example.com")]
    public void Domain_DropsWww_AndCoversSubdomains(string url, string domain) =>
        Assert.Equal(domain, NetscapeCookies.DomainFor(new Uri(url)));

    [Fact]
    public void Format_WritesOneLinePerCookie_AndSkipsBrokenPairs()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var text = NetscapeCookies.Format("a=1; b = two=2 ;junk; =x; c=b\tad", new Uri("https://www.youtube.com/"), now);
        Assert.Equal(
            "# Netscape HTTP Cookie File\n" +
            ".youtube.com\tTRUE\t/\tFALSE\t1086400\ta\t1\n" +
            ".youtube.com\tTRUE\t/\tFALSE\t1086400\tb\ttwo=2\n",
            text);
    }
}

public sealed class ChecksumsTests
{
    private const string A = "e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3";
    private const string B = "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d";

    [Fact]
    public void Sha256Sums_MatchesTheExactName()
    {
        var sums = $"{A}  yt-dlp\n{B}  yt-dlp.exe\n{A}  yt-dlp.tar.gz\n";
        Assert.Equal(B, Checksums.FindSha256(sums, "yt-dlp.exe"));
        Assert.Equal(A, Checksums.FindSha256(sums, "yt-dlp"));
        Assert.Null(Checksums.FindSha256(sums, "yt-dlp_x86.exe"));
        Assert.Equal(B, Checksums.FindSha256($"{B.ToUpperInvariant()} *yt-dlp.exe\r\n", "yt-dlp.exe"));
    }

    [Fact]
    public void GetFileHashLayout_AsDenoPublishesIt()
    {
        var deno = $"\r\nAlgorithm : SHA256\r\nHash      : {B.ToUpperInvariant()}\r\nPath      : C:\\a\\deno\\deno\\target\\release\\deno-x86_64-pc-windows-msvc.zip\r\n\r\n";
        Assert.Equal(B, Checksums.FindSha256(deno, "deno-x86_64-pc-windows-msvc.zip"));
    }
}
