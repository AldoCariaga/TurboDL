using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace TurboDL.Core.Ipc;

/// <summary>
/// Talks to a running TurboDL over its pipe: connect, send one JSON line, read one JSON reply line.
/// Used by a second launch of the app and by the native messaging host.
/// </summary>
public sealed class IpcClient(string? pipeName = null)
{
    private static readonly Lazy<string> DefaultName = new(() => PipeNameFor(CurrentUserId()));

    /// <summary>
    /// The pipe for this user: <c>TurboDL.Ipc.&lt;user SID&gt;</c>. Pipe names are machine-wide, so the SID keeps two
    /// signed-in users' copies of TurboDL apart. The app, a second launch and the native host all use this.
    /// </summary>
    public static string DefaultPipeName => DefaultName.Value;

    public static string PipeNameFor(string userId) => "TurboDL.Ipc." + userId;

    public string PipeName { get; } = pipeName ?? DefaultPipeName;

    /// <summary>How long to wait for the pipe to exist and have a free instance.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the reply once the request is sent.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends a request and returns the app's reply.
    /// Throws <see cref="IpcUnavailableException"/> if no pipe could be connected within <see cref="ConnectTimeout"/>
    /// (TurboDL isn't running, or is still starting), <see cref="TimeoutException"/> if it connected but didn't
    /// answer in time, <see cref="UnauthorizedAccessException"/> if the pipe belongs to another user,
    /// <see cref="IOException"/> (including <see cref="IpcProtocolException"/>) if the connection breaks,
    /// and <see cref="System.Text.Json.JsonException"/> if the reply isn't valid JSON.
    /// </summary>
    public async Task<IpcReply> SendAsync(IpcRequest request, CancellationToken ct = default) =>
        IpcJson.ParseReply(await SendLineAsync(IpcJson.Serialize(request), ct));

    /// <summary>Sends one raw JSON line (no line breaks) and returns the raw reply line. Same errors as <see cref="SendAsync"/>.</summary>
    public async Task<string> SendLineAsync(string jsonLine, CancellationToken ct = default)
    {
        // On Windows the server's owner is checked by hand (see IpcPipeSecurity): PipeOptions.CurrentUserOnly
        // would compare token owners and fail whenever only one side runs elevated.
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
        }
        catch (TimeoutException)
        {
            throw new IpcUnavailableException(PipeName);
        }
        if (OperatingSystem.IsWindows()) IpcPipeSecurity.VerifyServer(pipe);
        LetServerTakeForeground(pipe);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            await IpcLines.WriteLineAsync(pipe, jsonLine, timeout.Token);
            return await new IpcLineReader(pipe).ReadLineAsync(IpcLines.MaxLineBytes, timeout.Token)
                   ?? throw new IpcProtocolException("TurboDL closed the connection without replying.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"TurboDL didn't reply within {ReplyTimeout.TotalSeconds:0.#} s.");
        }
    }

    private static string CurrentUserId() =>
        OperatingSystem.IsWindows() ? IpcPipeSecurity.CurrentUser.Value : Environment.UserName;

    /// <summary>
    /// Windows only lets the foreground app hand focus on. We usually are it (a fresh launch, or the native host
    /// started by Chrome), so pass that right to the app so its window and Add dialog can come to the front.
    /// </summary>
    private static void LetServerTakeForeground(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) && pid != 0)
            AllowSetForegroundWindow(pid);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
