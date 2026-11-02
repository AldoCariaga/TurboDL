using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using TurboDL.Core.Ipc;
using TurboDL.NativeHost;

namespace TurboDL.Core.Tests;

/// <summary>The native host's loop, fed fake Chrome frames, talking to a test pipe server standing in for the app.</summary>
public sealed class NativeHostTests : IAsyncDisposable
{
    private readonly string _pipeName = "TurboDL.Ipc.HostTest." + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<IpcRequest> _received = new();
    private IpcPipeServer? _app;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>Plays the running app.</summary>
    private void StartApp()
    {
        _app = new IpcPipeServer((request, _) =>
        {
            _received.Enqueue(request);
            return Task.FromResult(request.Type == IpcCommands.GetConfig ? IpcReply.Config(["zip"], 100) : IpcReply.Success());
        }, _pipeName);
        _app.Start();
    }

    private sealed class FakeLauncher(Action? onLaunch = null) : IAppLauncher
    {
        public int Launches;

        public void Launch()
        {
            Launches++;
            onLaunch?.Invoke();
        }
    }

    private NativeHostLoop Host(IAppLauncher launcher) => new(launcher, HostLog.None)
    {
        PipeName = _pipeName,
        ProbeTimeout = TimeSpan.FromMilliseconds(300),
        StartupTimeout = TimeSpan.FromSeconds(3),
    };

    private static MemoryStream Frames(params string[] messages)
    {
        var stream = new MemoryStream();
        foreach (var m in messages)
        {
            // Raw framing: Chrome may send up to 64 MiB, so Encode's 1 MB host-to-Chrome limit doesn't apply here.
            var body = Encoding.UTF8.GetBytes(m);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            stream.Write(header);
            stream.Write(body);
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task<List<IpcReply>> RepliesAsync(MemoryStream output)
    {
        output.Position = 0;
        var replies = new List<IpcReply>();
        while (await NativeMessaging.ReadAsync(output) is { } json) replies.Add(IpcJson.ParseReply(json));
        return replies;
    }

    [Fact]
    public async Task Add_IsForwardedAsIs_AndTheLoopEndsAtEof()
    {
        StartApp();
        var launcher = new FakeLauncher();
        var add = """{"type":"add","url":"https://e.example/f.zip","filename":"f.zip","referrer":"https://e.example/","userAgent":"UA/1","cookies":"a=1; b=2","size":5}""";
        var output = new MemoryStream();

        var exit = await Host(launcher).RunAsync(Frames(add), output);

        Assert.Equal(NativeHostLoop.ExitOk, exit);
        Assert.True(Assert.Single(await RepliesAsync(output)).Ok);
        var got = Assert.Single(_received);
        Assert.Equal(add, IpcJson.Serialize(got));
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task SeveralMessages_GetReplies_InOrder()
    {
        StartApp();
        var output = new MemoryStream();
        await Host(new FakeLauncher()).RunAsync(Frames("""{"type":"getConfig"}""", "{broken", """{"type":"show"}"""), output);

        var replies = await RepliesAsync(output);
        Assert.Equal(3, replies.Count);
        Assert.Equal(new[] { "zip" }, replies[0].CatchExtensions);
        Assert.False(replies[1].Ok);
        Assert.Contains("Bad JSON", replies[1].Error);
        Assert.True(replies[2].Ok);
    }

    [Fact]
    public async Task PrettyPrintedJson_IsCompactedForThePipe()
    {
        StartApp();
        var reply = await Host(new FakeLauncher()).HandleAsync("{\n  \"type\": \"add\",\n  \"url\": \"https://e.example/x\"\n}");
        Assert.True(IpcJson.ParseReply(reply).Ok);
        Assert.Equal("https://e.example/x", Assert.Single(_received).Url);
    }

    [Fact]
    public async Task GetConfig_WithNoApp_FailsWithoutLaunching()
    {
        var launcher = new FakeLauncher();
        var reply = IpcJson.ParseReply(await Host(launcher).HandleAsync("""{"type":"getConfig"}"""));

        Assert.False(reply.Ok);
        Assert.Equal(NativeHostLoop.NotRunning, reply.Error);
        Assert.Equal(0, launcher.Launches);
    }

    [Theory]
    [InlineData("""{"type":"addVideo","url":"https://e.example/v"}""")]
    [InlineData("""{"url":"https://e.example/v"}""")]
    public async Task OtherMessages_WithNoApp_DontLaunchEither(string message)
    {
        var launcher = new FakeLauncher();
        var reply = IpcJson.ParseReply(await Host(launcher).HandleAsync(message));
        Assert.Equal(NativeHostLoop.NotRunning, reply.Error);
        Assert.Equal(0, launcher.Launches);
    }

    [Theory]
    [InlineData("""{"type":"add","url":"https://e.example/f.zip"}""")]
    [InlineData("""{"type":"show"}""")]
    public async Task AddOrShow_WithNoApp_LaunchesIt_ThenDelivers(string message)
    {
        var launcher = new FakeLauncher(onLaunch: () => Task.Delay(500).ContinueWith(_ => StartApp())); // app opens its pipe a bit later
        var reply = IpcJson.ParseReply(await Host(launcher).HandleAsync(message));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(1, launcher.Launches);
        Assert.Single(_received);
    }

    [Fact]
    public async Task Add_WhenTheAppNeverComesUp_ReportsIt()
    {
        var launcher = new FakeLauncher();
        var reply = IpcJson.ParseReply(await new NativeHostLoop(launcher, HostLog.None)
        {
            PipeName = _pipeName,
            ProbeTimeout = TimeSpan.FromMilliseconds(200),
            StartupTimeout = TimeSpan.FromMilliseconds(500),
        }.HandleAsync("""{"type":"show"}"""));

        Assert.False(reply.Ok);
        Assert.Contains("didn't start", reply.Error);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task Add_WhenTheLaunchFails_ReportsIt()
    {
        var launcher = new FakeLauncher(onLaunch: () => throw new FileNotFoundException("no TurboDL.App.exe here"));
        var reply = IpcJson.ParseReply(await Host(launcher).HandleAsync("""{"type":"add","url":"https://e.example/f"}"""));

        Assert.False(reply.Ok);
        Assert.Contains("Couldn't start TurboDL", reply.Error);
        Assert.Contains("no TurboDL.App.exe here", reply.Error);
    }

    [Fact]
    public async Task EmptyInput_ExitsCleanly_WithNoOutput()
    {
        var output = new MemoryStream();
        Assert.Equal(NativeHostLoop.ExitOk, await Host(new FakeLauncher()).RunAsync(new MemoryStream(), output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task GarbageFrame_RepliesWithAnError_ThenStops()
    {
        StartApp();
        var input = new MemoryStream();
        input.Write(NativeMessaging.Encode("""{"type":"show"}"""));
        var huge = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(huge, int.MaxValue);
        input.Write(huge);
        input.Write(Encoding.UTF8.GetBytes("this is not a frame"));
        input.Position = 0;
        var output = new MemoryStream();

        var exit = await Host(new FakeLauncher()).RunAsync(input, output);

        Assert.Equal(NativeHostLoop.ExitProtocolError, exit);
        var replies = await RepliesAsync(output);
        Assert.Equal(2, replies.Count);
        Assert.True(replies[0].Ok);
        Assert.False(replies[1].Ok);
        Assert.Contains("out of range", replies[1].Error);
    }

    [Fact]
    public async Task TruncatedFrame_RepliesWithAnError_ThenStops()
    {
        var input = new MemoryStream(NativeMessaging.Encode("""{"type":"show"}""")[..^4]);
        var output = new MemoryStream();

        Assert.Equal(NativeHostLoop.ExitProtocolError, await Host(new FakeLauncher()).RunAsync(input, output));
        Assert.False(Assert.Single(await RepliesAsync(output)).Ok);
    }

    [Fact]
    public async Task OversizedMessage_IsRefusedByThePipe_AndTheHostCarriesOn()
    {
        StartApp();
        var big = """{"type":"add","url":"https://e.example/f","cookies":"c=""" + new string('x', IpcLines.MaxLineBytes) + "\"}";
        var output = new MemoryStream();
        await Host(new FakeLauncher()).RunAsync(Frames(big, """{"type":"show"}"""), output);

        var replies = await RepliesAsync(output);
        Assert.Equal(2, replies.Count);
        Assert.False(replies[0].Ok);
        Assert.True(replies[1].Ok);
    }

    [Fact]
    public void AppPath_ComesFromHostSettings_ElseNextToTheHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "turbodl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, AppLauncher.AppExeName), AppLauncher.ResolveAppPath(dir));

            File.WriteAllText(Path.Combine(dir, AppLauncher.SettingsFileName), """{"appPath":"..\\app\\TurboDL.App.exe"}""");
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "..", "app", "TurboDL.App.exe")), AppLauncher.ResolveAppPath(dir));

            File.WriteAllText(Path.Combine(dir, AppLauncher.SettingsFileName), "{corrupt");
            Assert.Equal(Path.Combine(dir, AppLauncher.AppExeName), AppLauncher.ResolveAppPath(dir));

            var missing = new AppLauncher(Path.Combine(dir, "nope.exe"), HostLog.None);
            Assert.Throws<FileNotFoundException>(missing.Launch);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HostLog_WritesToItsFile_AndNeverThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "turbodl-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new HostLog(Path.Combine(dir, "logs", "host.log"));
            log.Write("hello");
            Assert.Contains("hello", File.ReadAllText(log.FilePath!));

            new HostLog(Path.Combine(dir, "logs", "host.log", "under-a-file.log")).Write("lost, but no exception");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
