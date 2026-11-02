using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TurboDL.Core.Tests;

/// <summary>
/// Local HTTP server serving one in-memory file at /file, with knobs for the awkward
/// things real servers do: no range support, no length, dropped connections, changed ETag.
/// </summary>
public sealed class TestFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _abortsLeft;
    private int _inFlight;

    /// <summary>Answer 429 to data requests beyond this many at once (0 = no limit), like many CDNs do.</summary>
    public int MaxConcurrent { get; set; }

    public byte[] Data { get; set; }
    public bool SupportRanges { get; set; } = true;
    public bool SendLength { get; set; } = true;
    public string? ETag { get; set; } = "\"v1\"";
    public string? ContentDisposition { get; set; }

    /// <summary>Server-side cap per response, in bytes/second (0 = none).</summary>
    public int ThrottleBytesPerSecond { get; set; }

    /// <summary>Data requests to cut off after <see cref="AbortAfterBytes"/> bytes.</summary>
    public int AbortRequests { set => _abortsLeft = value; }
    public int AbortAfterBytes { get; set; } = 100_000;

    /// <summary>Data requests that go silent (connection stays open, no more bytes) after <see cref="AbortAfterBytes"/>.</summary>
    public int StallRequests { set => _stallsLeft = value; }
    private int _stallsLeft;

    /// <summary>Start offsets of every ranged data request (the 0-0 probe excluded).</summary>
    public ConcurrentQueue<long> RangeStarts { get; } = new();

    public string Url { get; private set; } = "";

    private TestFileServer(byte[] data)
    {
        Data = data;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/file", Handle);
    }

    public static async Task<TestFileServer> StartAsync(byte[] data)
    {
        var server = new TestFileServer(data);
        await server._app.StartAsync();
        server.Url = server._app.Urls.First() + "/file";
        return server;
    }

    public static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        return data;
    }

    private async Task Handle(HttpContext ctx)
    {
        var res = ctx.Response;
        var len = (long)Data.Length;
        if (ETag is not null) res.Headers.ETag = ETag;
        if (ContentDisposition is not null) res.Headers.ContentDisposition = ContentDisposition;
        res.Headers.AcceptRanges = SupportRanges ? "bytes" : "none";
        res.ContentType = "application/octet-stream";

        long from = 0, to = len - 1;
        var range = ctx.Request.Headers.Range.ToString();
        var isProbe = range == "bytes=0-0";
        if (SupportRanges && range.StartsWith("bytes="))
        {
            var parts = range["bytes=".Length..].Split('-');
            from = long.Parse(parts[0]);
            if (parts[1].Length > 0) to = Math.Min(long.Parse(parts[1]), len - 1);
            if (from >= len)
            {
                res.StatusCode = 416;
                res.Headers.ContentRange = $"bytes */{len}";
                return;
            }
            res.StatusCode = 206;
            res.Headers.ContentRange = $"bytes {from}-{to}/{len}";
            if (!isProbe) RangeStarts.Enqueue(from);
        }

        if (!isProbe && MaxConcurrent > 0)
        {
            if (Interlocked.Increment(ref _inFlight) > MaxConcurrent)
            {
                Interlocked.Decrement(ref _inFlight);
                res.StatusCode = 429;
                res.Headers.Remove("Content-Range");
                return;
            }
            try { await SendAsync(ctx, from, to, isProbe); }
            finally { Interlocked.Decrement(ref _inFlight); }
            return;
        }

        await SendAsync(ctx, from, to, isProbe);
    }

    private async Task SendAsync(HttpContext ctx, long from, long to, bool isProbe)
    {
        var res = ctx.Response;
        var count = to - from + 1;
        if (SendLength) res.ContentLength = count;
        var abort = !isProbe && Interlocked.Decrement(ref _abortsLeft) >= 0;
        var stall = !isProbe && !abort && Interlocked.Decrement(ref _stallsLeft) >= 0;

        const int chunk = 16 * 1024;
        long sent = 0;
        while (sent < count)
        {
            var n = (int)Math.Min(chunk, count - sent);
            await res.Body.WriteAsync(Data.AsMemory((int)(from + sent), n), ctx.RequestAborted);
            sent += n;
            if (abort && sent >= AbortAfterBytes)
            {
                await res.Body.FlushAsync();
                ctx.Abort();
                return;
            }
            if (stall && sent >= AbortAfterBytes)
            {
                await res.Body.FlushAsync();
                try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); } catch (OperationCanceledException) { }
                return;
            }
            if (ThrottleBytesPerSecond > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)n / ThrottleBytesPerSecond), ctx.RequestAborted);
        }
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
