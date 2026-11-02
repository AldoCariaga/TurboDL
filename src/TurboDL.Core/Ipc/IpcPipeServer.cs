using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text.Json;

namespace TurboDL.Core.Ipc;

/// <summary>
/// The app's end of the pipe: accepts any number of connections for as long as it runs, reads JSON request lines,
/// and answers each with one JSON reply line. Malformed or invalid requests get <c>{"ok":false,"error"}</c>;
/// only well-formed ones reach the handler. Nothing a client sends can take the server down.
/// </summary>
public sealed class IpcPipeServer : IAsyncDisposable
{
    private readonly Func<IpcRequest, CancellationToken, Task<IpcReply>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private long _nextConnection;
    private Task? _acceptLoop;

    /// <param name="pipeName">Null for <see cref="IpcClient.DefaultPipeName"/>, this user's pipe.</param>
    public IpcPipeServer(Func<IpcRequest, CancellationToken, Task<IpcReply>> handler, string? pipeName = null)
    {
        _handler = handler;
        PipeName = pipeName ?? IpcClient.DefaultPipeName;
    }

    public string PipeName { get; }

    /// <summary>A connection is dropped if it sends nothing for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised (on a pool thread) for problems worth logging: a handler that threw, a pipe that couldn't be created.</summary>
    public event Action<Exception>? Error;

    /// <summary>
    /// Opens the pipe and starts accepting. Throws if the pipe name is already taken (another copy, or something
    /// squatting on it), so the caller knows clients won't reach it.
    /// </summary>
    public void Start()
    {
        if (_acceptLoop is not null) throw new InvalidOperationException("The server is already running.");
        var first = CreateInstance(firstInstance: true);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    private NamedPipeServerStream CreateInstance(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous;
        if (firstInstance) options |= PipeOptions.FirstPipeInstance;

        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly);

        // Only this user's account, by its own SID, owns and may open the pipe. Not PipeOptions.CurrentUserOnly:
        // that uses the token's *owner*, which is the Administrators group when elevated, and would then lock out
        // the same user's normal processes (Chrome's native host). .NET also rejects remote clients.
        var user = IpcPipeSecurity.CurrentUser;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken ct)
    {
        NamedPipeServerStream? listener = first;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                listener ??= CreateInstance(firstInstance: false);
                await listener.WaitForConnectionAsync(ct);
                var connection = listener;
                listener = null; // the next turn opens a fresh instance for the next client straight away
                Track(ServeAsync(connection, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                listener?.Dispose();
                listener = null;
                Error?.Invoke(ex);
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        listener?.Dispose();
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _nextConnection);
        _connections[id] = task;
        task.ContinueWith(_ => _connections.TryRemove(id, out Task? _), TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using (pipe)
            {
                var reader = new IpcLineReader(pipe);
                while (true)
                {
                    string? line;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        idle.CancelAfter(IdleTimeout);
                        try
                        {
                            line = await reader.ReadLineAsync(IpcLines.MaxLineBytes, idle.Token);
                        }
                        catch (IpcProtocolException ex)
                        {
                            // Can't find the next line boundary reliably after this, so answer and hang up.
                            await IpcLines.WriteLineAsync(pipe, IpcJson.Serialize(IpcReply.Fail(ex.Message)), ct);
                            return;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return; // idle client
                        }
                    }
                    if (line is null) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var reply = await DispatchAsync(line, ct);
                    await IpcLines.WriteLineAsync(pipe, IpcJson.Serialize(reply), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The client went away mid-exchange; nothing to answer.
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private async Task<IpcReply> DispatchAsync(string line, CancellationToken ct)
    {
        IpcRequest request;
        try
        {
            request = IpcJson.ParseRequest(line);
        }
        catch (JsonException ex)
        {
            return IpcReply.Fail($"Bad JSON: {ex.Message}");
        }

        if (request.Validate() is { } problem) return IpcReply.Fail(problem);

        try
        {
            return await _handler(request, ct) ?? IpcReply.Fail("No reply from the handler.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return IpcReply.Fail("TurboDL is shutting down.");
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            return IpcReply.Fail(ex.Message);
        }
    }

    /// <summary>Stops accepting, cancels open connections and waits briefly for them to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        var pending = _connections.Values.ToList();
        if (_acceptLoop is not null) pending.Add(_acceptLoop);
        // Not disposing _stop: a connection still finishing after the wait may yet link to its token.
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(2)));
    }
}
