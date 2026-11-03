using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TurboDL.Core.Engine;
using TurboDL.Core.Ipc;
using TurboDL.Core.Model;
using TurboDL.Core.Storage;

namespace TurboDL.Core.Tests;

public sealed class NativeMessagingTests
{
    [Fact]
    public async Task RoundTrip_PreservesMessages_InOrder()
    {
        var messages = new[] { """{"type":"show"}""", """{"type":"add","url":"https://e.example/ünï 文件.zip"}""", "{}" };
        var stream = new MemoryStream();
        foreach (var m in messages) await NativeMessaging.WriteAsync(stream, m);

        stream.Position = 0;
        foreach (var m in messages) Assert.Equal(m, await NativeMessaging.ReadAsync(stream));
        Assert.Null(await NativeMessaging.ReadAsync(stream)); // clean end of stream
    }

    [Fact]
    public void Encode_WritesLittleEndianByteLength()
    {
        var json = """{"a":"é"}"""; // é is 2 bytes in UTF-8
        var frame = NativeMessaging.Encode(json);

        Assert.Equal(Encoding.UTF8.GetByteCount(json), BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(new byte[] { 10, 0, 0, 0 }, frame[..4]);
        Assert.Equal(json, Encoding.UTF8.GetString(frame, 4, frame.Length - 4));
    }

    [Fact]
    public async Task Write_AllowsExactlyOneMegabyte_AndRefusesMore()
    {
        var exact = Json(NativeMessaging.MaxOutgoingBytes);
        var stream = new MemoryStream();
        await NativeMessaging.WriteAsync(stream, exact);
        Assert.Equal(4 + NativeMessaging.MaxOutgoingBytes, stream.Length);
        stream.Position = 0;
        Assert.Equal(exact, await NativeMessaging.ReadAsync(stream));

        var tooBig = new MemoryStream();
        await Assert.ThrowsAsync<IpcProtocolException>(() => NativeMessaging.WriteAsync(tooBig, Json(NativeMessaging.MaxOutgoingBytes + 1)));
        Assert.Equal(0, tooBig.Length); // nothing half-written for Chrome to choke on

        // Multi-byte characters count as bytes, not chars.
        var wide = "\"" + new string('é', NativeMessaging.MaxOutgoingBytes / 2) + "\"";
        Assert.Throws<IpcProtocolException>(() => NativeMessaging.Encode(wide));

        // A JSON string of exactly n UTF-8 bytes.
        static string Json(int bytes) => "\"" + new string('x', bytes - 2) + "\"";
    }

    [Fact]
    public async Task Read_RejectsTruncatedAndCorruptFrames()
    {
        await Assert.ThrowsAsync<IpcProtocolException>(() => NativeMessaging.ReadAsync(new MemoryStream([5, 0])));

        var truncated = NativeMessaging.Encode("""{"type":"show"}""")[..^3];
        await Assert.ThrowsAsync<IpcProtocolException>(() => NativeMessaging.ReadAsync(new MemoryStream(truncated)));

        var huge = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(huge, NativeMessaging.MaxIncomingBytes + 1);
        await Assert.ThrowsAsync<IpcProtocolException>(() => NativeMessaging.ReadAsync(new MemoryStream(huge)));

        var negative = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        await Assert.ThrowsAsync<IpcProtocolException>(() => NativeMessaging.ReadAsync(new MemoryStream(negative)));

        var badUtf8 = new byte[] { 2, 0, 0, 0, 0xC3, 0x28 };
        await Assert.ThrowsAsync<IpcProtocolException>(() => NativeMessaging.ReadAsync(new MemoryStream(badUtf8)));
    }
}

public sealed class IpcJsonTests
{
    [Fact]
    public void Serialize_UsesContractNames_AndOmitsNulls()
    {
        var add = IpcJson.Serialize(IpcRequest.Add("https://e.example/a.zip", "a.zip", "https://e.example/", "UA/1", "k=v", 42));
        Assert.Equal(
            """{"type":"add","url":"https://e.example/a.zip","filename":"a.zip","referrer":"https://e.example/","userAgent":"UA/1","cookies":"k=v","size":42}""",
            add);
        Assert.Equal("""{"type":"show"}""", IpcJson.Serialize(IpcRequest.Show()));
        Assert.Equal("""{"ok":true}""", IpcJson.Serialize(IpcReply.Success()));
        Assert.Equal("""{"ok":false,"error":"nope"}""", IpcJson.Serialize(IpcReply.Fail("nope")));
        Assert.Equal("""{"ok":true,"catchExtensions":["zip","iso"],"minCatchSizeKB":512}""",
            IpcJson.Serialize(IpcReply.Config(["zip", "iso"], 512)));
    }

    [Fact]
    public void Parse_IsLenientAboutExtrasAndNumbers_ButNotMalformedJson()
    {
        var req = IpcJson.ParseRequest("""{"type":"add","url":"https://e.example/f","size":"123","tabId":7}""");
        Assert.Equal(123, req.Size);
        Assert.Null(req.Validate());

        Assert.ThrowsAny<JsonException>(() => IpcJson.ParseRequest("{not json"));
        Assert.ThrowsAny<JsonException>(() => IpcJson.ParseRequest("null"));
        Assert.ThrowsAny<JsonException>(() => IpcJson.ParseRequest("[1,2]"));
    }

    [Theory]
    [InlineData("""{"url":"https://e.example/f"}""", "no \"type\"")]
    [InlineData("""{"type":"addPlaylist","url":"https://e.example/f"}""", "Unknown command")]
    [InlineData("""{"type":"addVideo"}""", "\"addVideo\" needs a \"url\"")]
    [InlineData("""{"type":"addVideo","url":"blob:https://e.example/1"}""", "http://")]
    [InlineData("""{"type":"addVideo","url":"https://e.example/v","pageUrl":"file:///c:/x"}""", "\"pageUrl\"")]
    [InlineData("""{"type":"add"}""", "needs a \"url\"")]
    [InlineData("""{"type":"add","url":"ftp://e.example/f"}""", "http://")]
    [InlineData("""{"type":"add","url":"not a url"}""", "http://")]
    public void Validate_ExplainsWhatIsWrong(string json, string expected) =>
        Assert.Contains(expected, IpcJson.ParseRequest(json).Validate());

    [Fact]
    public void AddVideo_IsKnown_Valid_AndUsesContractNames()
    {
        Assert.True(IpcCommands.IsKnown("addVideo"));
        var req = IpcRequest.AddVideo("https://v.example/watch?v=1", "https://v.example/page", null, "UA/1", "a=1");
        Assert.Null(req.Validate());
        Assert.Equal(
            """{"type":"addVideo","url":"https://v.example/watch?v=1","pageUrl":"https://v.example/page","userAgent":"UA/1","cookies":"a=1"}""",
            IpcJson.Serialize(req));

        var parsed = IpcJson.ParseRequest("""{"type":"addVideo","url":"https://v.example/v","pageUrl":"https://v.example/p"}""");
        Assert.Equal("https://v.example/p", parsed.PageUrl);
        Assert.Null(parsed.Validate());
    }

    [Fact]
    public void BrowserHeaders_UseThePageAsReferer_WhenThereIsNone()
    {
        var headers = IpcRequest.AddVideo("https://cdn.example/v.mp4", "https://v.example/page").BrowserHeaders();
        Assert.Equal("https://v.example/page", headers["Referer"]);

        var explicitReferrer = IpcRequest.AddVideo("https://cdn.example/v.mp4", "https://v.example/page", "https://r.example/").BrowserHeaders();
        Assert.Equal("https://r.example/", explicitReferrer["Referer"]);
    }

    [Fact]
    public void BrowserHeaders_MapsToHttpHeaderNames_AndSkipsBlanks()
    {
        var headers = IpcRequest.Add("https://e.example/f", referrer: " https://r.example/ ", userAgent: "UA/2", cookies: "  ").BrowserHeaders();
        Assert.Equal(2, headers.Count);
        Assert.Equal("https://r.example/", headers["Referer"]);
        Assert.Equal("UA/2", headers["User-Agent"]);
    }
}

public sealed class IpcPipeTests : IAsyncDisposable
{
    private readonly string _pipeName = "TurboDL.Ipc.Test." + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<IpcRequest> _received = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly IpcPipeServer _server;

    public IpcPipeTests()
    {
        _server = new IpcPipeServer(HandleAsync, _pipeName);
        _server.Error += _errors.Enqueue;
        _server.Start();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private Task<IpcReply> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        _received.Enqueue(request);
        return request.Type switch
        {
            IpcCommands.GetConfig => Task.FromResult(IpcReply.Config(["zip", "iso"], 256)),
            IpcCommands.Add when request.Url!.Contains("throw") => throw new InvalidOperationException("handler blew up"),
            IpcCommands.Add when request.Url!.Contains("hang") => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => IpcReply.Success()),
            _ => Task.FromResult(IpcReply.Success()),
        };
    }

    private IpcClient Client => new(_pipeName) { ConnectTimeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task Commands_RoundTrip_WithEveryField()
    {
        Assert.True((await Client.SendAsync(IpcRequest.Show())).Ok);

        var config = await Client.SendAsync(IpcRequest.GetConfig());
        Assert.True(config.Ok);
        Assert.Equal(new[] { "zip", "iso" }, config.CatchExtensions);
        Assert.Equal(256, config.MinCatchSizeKB);

        var add = IpcRequest.Add("https://e.example/dl?id=1", "Setup 文件.exe", "https://e.example/page", "Mozilla/5.0 (X)",
            "sid=abc; theme=dark", 1_234_567);
        Assert.True((await Client.SendAsync(add)).Ok);

        var got = _received.Last();
        Assert.Equal(IpcJson.Serialize(add), IpcJson.Serialize(got));
    }

    [Theory]
    [InlineData("{oops", "Bad JSON")]
    [InlineData("42", "Bad JSON")]
    [InlineData("""{"type":"launchMissiles"}""", "Unknown command")]
    [InlineData("""{"type":"add","url":"javascript:alert(1)"}""", "http://")]
    [InlineData("""{"type":"add","url":"https://e.example/throw"}""", "handler blew up")]
    public async Task BadRequests_GetAnError_AndTheServerCarriesOn(string line, string expected)
    {
        var reply = IpcJson.ParseReply(await Client.SendLineAsync(line));
        Assert.False(reply.Ok);
        Assert.Contains(expected, reply.Error);

        Assert.True((await Client.SendAsync(IpcRequest.Show())).Ok);
    }

    [Fact]
    public async Task OversizedLine_IsRefused_WithoutReadingItAll()
    {
        await using var pipe = await ConnectRawAsync();
        var junk = new byte[IpcLines.MaxLineBytes + 10];
        junk.AsSpan().Fill((byte)'x');
        var write = pipe.WriteAsync(junk).AsTask(); // the server hangs up partway, so this may fail

        var reply = await new IpcLineReader(pipe).ReadLineAsync();
        Assert.Contains("longer than", IpcJson.ParseReply(reply!).Error);
        try { await write.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { /* broken pipe: the server hung up, as intended */ }

        Assert.True((await Client.SendAsync(IpcRequest.Show())).Ok);
    }

    [Fact]
    public async Task OneConnection_CanCarrySeveralRequests()
    {
        await using var pipe = await ConnectRawAsync();
        var reader = new IpcLineReader(pipe);
        await IpcLines.WriteLineAsync(pipe, """{"type":"show"}""");
        Assert.True(IpcJson.ParseReply((await reader.ReadLineAsync())!).Ok);
        await IpcLines.WriteLineAsync(pipe, """{"type":"getConfig"}""");
        Assert.Equal(256, IpcJson.ParseReply((await reader.ReadLineAsync())!).MinCatchSizeKB);
    }

    [Fact]
    public async Task ManyClientsAtOnce_AllGetReplies()
    {
        var replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => Client.SendAsync(IpcRequest.Add($"https://e.example/{i}.zip")))));

        Assert.All(replies, r => Assert.True(r.Ok));
        Assert.Equal(40, _received.Count(r => r.Type == IpcCommands.Add));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task SecondServer_OnTheSameName_RefusesToStart()
    {
        await using var other = new IpcPipeServer(HandleAsync, _pipeName);
        Assert.ThrowsAny<Exception>(other.Start);

        Assert.True((await Client.SendAsync(IpcRequest.Show())).Ok); // the first one is unaffected
    }

    [Fact]
    public async Task Pipe_IsOwnedByAndOpenToThisUserOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var pipe = await ConnectRawAsync();
        var acl = pipe.GetAccessControl();
        var me = WindowsIdentity.GetCurrent().User;

        Assert.Equal(me, acl.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>());
        Assert.Equal(me, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }

    [Fact]
    public void DefaultPipeName_IsPerUser()
    {
        var name = IpcClient.DefaultPipeName;
        var expected = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : Environment.UserName;
        Assert.Equal("TurboDL.Ipc." + expected, name);
        if (OperatingSystem.IsWindows()) Assert.Matches(@"^TurboDL\.Ipc\.S-1-5-21(-\d+)+$", name);
        Assert.Equal(name, new IpcClient().PipeName);
        Assert.Equal(name, IpcClient.DefaultPipeName); // stable
    }

    [Fact]
    public async Task NoServer_TimesOut()
    {
        var client = new IpcClient("TurboDL.Ipc.Test.Nobody." + Guid.NewGuid().ToString("N")) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        await Assert.ThrowsAsync<IpcUnavailableException>(() => client.SendAsync(IpcRequest.Show()));
    }

    [Fact]
    public async Task ConnectedButNoReply_IsAPlainTimeout_NotUnavailable()
    {
        var client = new IpcClient(_pipeName) { ReplyTimeout = TimeSpan.FromMilliseconds(300) };
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(IpcRequest.Add("https://e.example/hang")));
        Assert.Contains("didn't reply", ex.Message);
    }

    [Fact]
    public async Task AfterDispose_ClientsCanNoLongerConnect()
    {
        await _server.DisposeAsync();
        var client = new IpcClient(_pipeName) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        await Assert.ThrowsAsync<IpcUnavailableException>(() => client.SendAsync(IpcRequest.Show()));
    }

    private async Task<NamedPipeClientStream> ConnectRawAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return pipe;
    }
}

/// <summary>A caught browser download's cookies, referrer and user agent must reach the server on every request.</summary>
public sealed class BrowserHeadersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "turbodl-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task HeadersFromAddRequest_SurviveTheStore_AndReachProbeAndEverySegment()
    {
        var data = TestFileServer.RandomBytes(3 * 1024 * 1024);
        await using var server = await TestFileServer.StartAsync(data);
        using var http = TurboHttp.CreateClient();

        // As it arrives over the pipe.
        var request = IpcJson.ParseRequest(IpcJson.Serialize(IpcRequest.Add(server.Url, "caught.bin",
            "https://site.example/page", "Mozilla/5.0 TurboDL-Test", "session=s3cr3t; lang=en")));

        // Probe, as the Add dialog does.
        var probe = await UrlProbe.ProbeAsync(http, request.Url!, request.BrowserHeaders(), CancellationToken.None);
        Assert.Equal(data.Length, probe.Length);

        // Add, persist and reload, as the app does on restart, then download on several connections.
        Directory.CreateDirectory(_dir);
        DownloadInfo info;
        using (var store = new DownloadStore(Path.Combine(_dir, "t.db")))
        {
            store.Add(new DownloadInfo
            {
                Url = request.Url!, FileName = request.Filename!, Directory = _dir, Connections = 4,
                Headers = request.BrowserHeaders(),
            });
            info = store.LoadAll().Single();
        }
        await new DownloadTask(info, http, new DownloadOptions { MinSegmentSize = 256 * 1024 }).RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, info.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "caught.bin")));
        var seen = server.RequestHeaders.ToList();
        Assert.True(seen.Count >= 3, $"expected the probe plus several segment requests, got {seen.Count}");
        Assert.All(seen, h =>
        {
            Assert.Equal("session=s3cr3t; lang=en", h["Cookie"]);
            Assert.Equal("https://site.example/page", h["Referer"]);
            Assert.Equal("Mozilla/5.0 TurboDL-Test", h["User-Agent"]);
        });
    }

    [Fact]
    public void Quit_IsKnown_Valid_AndRoundTrips()
    {
        Assert.True(IpcCommands.IsKnown("quit"));
        Assert.Equal("""{"type":"quit"}""", IpcJson.Serialize(IpcRequest.Quit()));
        var parsed = IpcJson.ParseRequest("""{"type":"quit"}""");
        Assert.Equal(IpcCommands.Quit, parsed.Type);
        Assert.Null(parsed.Validate());
    }
}
