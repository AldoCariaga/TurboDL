using System.Diagnostics;
using System.IO.Compression;
using TurboDL.Core.Archives;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

/// <summary>ArchiveExtractor against the fake 7z (tests/FakeSevenZip). One class, so the runs don't overlap.</summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TurboDL.ArchiveTests." + Guid.NewGuid().ToString("N"));
    private readonly string _sevenZip;

    public ArchiveExtractorTests()
    {
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(tools);
        var bin = AppContext.BaseDirectory;
        foreach (var f in new[] { "FakeSevenZip.dll", "FakeSevenZip.runtimeconfig.json", "FakeSevenZip.deps.json" })
            File.Copy(Path.Combine(bin, f), Path.Combine(tools, f));
        // The apphost finds FakeSevenZip.dll by its embedded name, so it can be renamed.
        _sevenZip = Path.Combine(tools, "7z.exe");
        File.Copy(Path.Combine(bin, "FakeSevenZip.exe"), _sevenZip);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Archive(string content, string name = "data.part01.rar")
    {
        var dir = Path.Combine(_root, "in");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Out(string name = "out") => Path.Combine(_root, name);

    private bool FakeStillRunning()
    {
        foreach (var p in Process.GetProcessesByName("7z"))
        {
            using (p)
            {
                try
                {
                    if (!p.HasExited && string.Equals(p.MainModule?.FileName, _sevenZip, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited meanwhile, or another user's process.
                }
            }
        }
        return false;
    }

    [Theory]
    [InlineData(@"C:\Down loads\my set.part01.rar", @"C:\Down loads\my set")]
    [InlineData(@"C:\Téléchargements\動画 アーカイブ.7z.001", @"D:\出力 フォルダ\x")]
    public void BuildArguments_IsExactlyTheExpectedList(string firstVolume, string output)
    {
        Assert.Equal(
            ["x", "-y", "-aou", "-bso0", "-bsp1", "-pturbodl-no-password", "-o" + output, "--", firstVolume],
            ArchiveExtractor.BuildArguments(firstVolume, output));
    }

    [Theory]
    [InlineData("  0%", 0, null)]
    [InlineData(" 45%", 45, null)]
    [InlineData(" 45% 12", 45, null)]
    [InlineData(" 45% 12 - Game\\data\\big file.bin", 45, "Game\\data\\big file.bin")]
    [InlineData("100% 3 - setup.exe", 100, "setup.exe")]
    public void TryParseProgress_ReadsSevenZipsProgress(string piece, int percent, string? file)
    {
        Assert.True(ArchiveExtractor.TryParseProgress(piece, out var progress));
        Assert.Equal(new ExtractProgress(percent, file), progress);
    }

    [Theory]
    [InlineData("                    ")]
    [InlineData("Extracting archive: x.rar")]
    [InlineData("101%")]
    [InlineData("45 %")]
    public void TryParseProgress_IgnoresEverythingElse(string piece)
    {
        Assert.False(ArchiveExtractor.TryParseProgress(piece, out _));
    }

    [Fact]
    public async Task Extract_ReportsEachPercentageOnce_AsSevenZipRedrawsIt()
    {
        var reports = new List<ExtractProgress>();
        var progress = new InlineProgress<ExtractProgress>(p => { lock (reports) reports.Add(p); });

        var result = await ArchiveExtractor.ExtractAsync(_sevenZip, Archive("plain archive"), Out(), TimeSpan.FromSeconds(30),
            CancellationToken.None, progress);

        Assert.True(result.Ok);
        Assert.Equal([0, 37, 82, 100], reports.Select(r => r.Percent));
        Assert.Equal("hello.txt", reports[1].File);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task Extract_Ok_WritesOutput_AndPassesTheArgumentListAsIs()
    {
        var archive = Archive("plain archive", "my set ✓ 動画.part01.rar");
        var output = Out("out folder ✓");

        var result = await ArchiveExtractor.ExtractAsync(_sevenZip, archive, output, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Equal("extracted from my set ✓ 動画.part01.rar", File.ReadAllText(Path.Combine(output, "hello.txt")));
        Assert.Equal(ArchiveExtractor.BuildArguments(archive, output), File.ReadAllLines(archive + ".args"));
    }

    [Fact]
    public async Task Extract_Warning_IsOk_WithOneWarning()
    {
        var output = Out();
        var result = await ArchiveExtractor.ExtractAsync(_sevenZip, Archive("WARN"), output, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["WARNING: Headers Error"], result.Warnings);
        Assert.True(File.Exists(Path.Combine(output, "hello.txt")));
    }

    [Fact]
    public async Task Extract_DataError_IsNotOk_Damaged()
    {
        var result = await ArchiveExtractor.ExtractAsync(_sevenZip, Archive("FAIL"), Out(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("damaged", result.Error);
    }

    [Fact]
    public async Task Extract_WrongPassword_IsNotOk_PasswordProtected()
    {
        var result = await ArchiveExtractor.ExtractAsync(_sevenZip, Archive("PASSWORD"), Out(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("password-protected", result.Error);
    }

    [Fact]
    public async Task Extract_Cancelled_Throws_AndKills7Zip()
    {
        var archive = Archive("SLOW");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveExtractor.ExtractAsync(_sevenZip, archive, Out(), null, cts.Token));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.True(File.Exists(archive + ".args"), "7-Zip never started");
        Assert.False(FakeStillRunning(), "7-Zip still running");
    }

    [Fact]
    public async Task Extract_Timeout_IsNotOk_DidntFinishInTime()
    {
        var sw = Stopwatch.StartNew();
        var result = await ArchiveExtractor.ExtractAsync(_sevenZip, Archive("SLOW"), Out(), TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("7-Zip didn't finish in time.", result.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.False(FakeStillRunning(), "7-Zip still running");
    }

    [Fact]
    public void ChooseOutputDirectory_MissingFolder_IsTheBaseName()
    {
        Assert.Equal(Path.Combine(_root, "set"), ArchiveExtractor.ChooseOutputDirectory(_root, "set"));
    }

    [Fact]
    public void ChooseOutputDirectory_EmptyFolder_IsTheBaseName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "set"));
        Assert.Equal(Path.Combine(_root, "set"), ArchiveExtractor.ChooseOutputDirectory(_root, "set"));
    }

    [Fact]
    public void ChooseOutputDirectory_NonEmptyFolder_Is2()
    {
        Directory.CreateDirectory(Path.Combine(_root, "set"));
        File.WriteAllText(Path.Combine(_root, "set", "a.txt"), "a");
        Assert.Equal(Path.Combine(_root, "set (2)"), ArchiveExtractor.ChooseOutputDirectory(_root, "set"));
    }

    [Fact]
    public void ChooseOutputDirectory_2AlsoNonEmpty_Is3()
    {
        foreach (var name in new[] { "set", "set (2)" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name, "sub"));
        }
        Assert.Equal(Path.Combine(_root, "set (3)"), ArchiveExtractor.ChooseOutputDirectory(_root, "set"));
    }

    [Theory]
    [InlineData(2, "ERROR: Wrong password : secret.txt", "The archive is password-protected. TurboDL can't extract it; open it with 7-Zip and enter the password.")]
    [InlineData(2, "ERROR: Data Error in ENCRYPTED file. Wrong password? : a.bin", "The archive is password-protected. TurboDL can't extract it; open it with 7-Zip and enter the password.")]
    [InlineData(2, "ERROR: Missing volume : data.part03.rar", "A part is missing or incomplete. Check that every part downloaded fully.")]
    [InlineData(2, "ERROR: unexpected end of archive", "A part is missing or incomplete. Check that every part downloaded fully.")]
    [InlineData(2, "WARNING: There are some data after the end of the payload data", "A part is missing or incomplete. Check that every part downloaded fully.")]
    [InlineData(2, "ERROR: CRC Failed : a.bin", "A part is damaged (CRC error). Download the parts again.")]
    [InlineData(2, "ERROR: Data Error : data.part01.rar", "A part is damaged (CRC error). Download the parts again.")]
    [InlineData(2, "ERROR: Headers Error", "A part is damaged (CRC error). Download the parts again.")]
    [InlineData(2, "ERROR: x.rar : Can not open the file as archive", "This file isn't an archive 7-Zip can open.")]
    [InlineData(2, "ERROR: x.rar : Cannot open the file as archive", "This file isn't an archive 7-Zip can open.")]
    [InlineData(8, "ERROR: Can't allocate required memory!", "7-Zip ran out of memory.")]
    [InlineData(7, "Command Line Error: Unknown switch: -q", "7-Zip failed (exit code 7): Command Line Error: Unknown switch: -q")]
    public void DescribeError_MapsEachKnownMessage(int exitCode, string line, string expected)
    {
        Assert.Equal(expected, ArchiveExtractor.DescribeError(exitCode, ["", "Scanning the drive for archives:", line, ""]));
    }

    [Fact]
    public void DescribeError_NoStderr_GivesTheExitCode()
    {
        Assert.Equal("7-Zip failed (exit code 255).", ArchiveExtractor.DescribeError(255, []));
    }

    /// <summary>Runs only when TURBODL_7Z points at a real 7z.exe; otherwise it returns at once.</summary>
    [Fact]
    public async Task RealSevenZip_ExtractsMultiVolume7z()
    {
        var real = Environment.GetEnvironmentVariable("TURBODL_7Z");
        if (string.IsNullOrEmpty(real) || !File.Exists(real)) return;

        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        var files = new Dictionary<string, byte[]>
        {
            ["a.bin"] = RandomBytes(150_000),
            ["b ✓.bin"] = RandomBytes(90_000),
        };
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(src, name), bytes);

        // A 7z volume set.
        var sets = Path.Combine(_root, "sets");
        Directory.CreateDirectory(sets);
        var make = await ToolProcess.RunAsync(real, ["a", "-v64k", Path.Combine(sets, "set.7z"), .. files.Keys.Select(n => Path.Combine(src, n))],
            null, null, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(0, make.ExitCode);
        Assert.True(File.Exists(Path.Combine(sets, "set.7z.002")), "7z made only one volume");

        var out7z = Out("out7z");
        var result = await ArchiveExtractor.ExtractAsync(real, Path.Combine(sets, "set.7z.001"), out7z, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        foreach (var (name, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(out7z, name)));

        // A byte-split stored ZIP, cut into three pieces the way the test kit's --format zip does.
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes) in files)
                {
                    using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    entry.Write(bytes);
                }
            }
            zip = ms.ToArray();
        }
        var piece = (zip.Length + 2) / 3;
        for (var i = 0; i < 3; i++)
        {
            var start = i * piece;
            File.WriteAllBytes(Path.Combine(sets, $"set2.zip.{i + 1:000}"), zip[start..Math.Min(zip.Length, start + piece)]);
        }

        var outZip = Out("outzip");
        result = await ArchiveExtractor.ExtractAsync(real, Path.Combine(sets, "set2.zip.001"), outZip, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        foreach (var (name, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(outZip, name)));
    }

    private static byte[] RandomBytes(int length) => System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
}
