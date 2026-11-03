using System.Text;
using System.Text.Json;
using TurboDL.Core.Ipc;

namespace TurboDL.NativeHost;

/// <summary>Starts TurboDL when a message needs it and it isn't running.</summary>
public interface IAppLauncher
{
    /// <summary>Starts the app (quietly, in the tray). Throws if it can't.</summary>
    void Launch();
}

/// <summary>
/// The native messaging host's whole job: read Chrome's framed messages from stdin, forward each one's JSON to
/// TurboDL's pipe as-is, and frame the reply back to stdout. Runs until Chrome closes stdin.
/// Every message gets exactly one reply, <c>{"ok":false,"error"}</c> when anything goes wrong.
/// </summary>
public sealed class NativeHostLoop(IAppLauncher launcher, HostLog log)
{
    public const int ExitOk = 0;

    /// <summary>The stream broke the framing (garbage length, truncated message); nothing after it can be trusted.</summary>
    public const int ExitProtocolError = 1;

    public const string NotRunning = "TurboDL is not running";

    public const string QuitRefused = "\"quit\" isn't allowed from the browser.";

    /// <summary>Null = this user's pipe.</summary>
    public string? PipeName { get; init; }

    /// <summary>How long to try the pipe before deciding TurboDL isn't running. It answers at once when it is.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a freshly launched TurboDL gets to open its pipe.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        while (true)
        {
            string? message;
            try
            {
                message = await NativeMessaging.ReadAsync(input, ct);
            }
            catch (IpcProtocolException ex)
            {
                log.Write($"Bad frame from Chrome, stopping: {ex.Message}");
                await TryWriteAsync(output, Fail(ex.Message), ct);
                return ExitProtocolError;
            }

            if (message is null)
            {
                log.Write("Chrome closed the port.");
                return ExitOk;
            }

            var reply = await HandleAsync(message, ct);
            if (!await TryWriteAsync(output, reply, ct)) return ExitProtocolError;
        }
    }

    /// <summary>One message in, one reply out (both JSON). Never throws, except on cancellation.</summary>
    public async Task<string> HandleAsync(string message, CancellationToken ct = default)
    {
        string type, line;
        try
        {
            (type, line) = Inspect(message);
        }
        catch (JsonException ex)
        {
            log.Write($"Bad JSON from Chrome: {ex.Message}");
            return Fail($"Bad JSON: {ex.Message}");
        }

        // quit is for TurboDL Setup on this PC; a web page talking to the extension must never close the app.
        if (type.Equals(IpcCommands.Quit, StringComparison.OrdinalIgnoreCase))
        {
            log.Write($"{type}: refused, not allowed from the browser.");
            return Fail(QuitRefused);
        }

        try
        {
            return Logged(type, await SendAsync(line, ProbeTimeout, ct));
        }
        catch (IpcUnavailableException)
        {
            // Not running: carry on below.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Write($"{type}: {ex.GetType().Name}: {ex.Message}");
            return Fail(ex.Message);
        }

        // Only a user action (a caught download, the context menu) may start TurboDL. The extension polls
        // getConfig in the background, and starting the app for that would bring it back every time it's quit.
        if (type is not (IpcCommands.Add or IpcCommands.AddVideo or IpcCommands.Show))
        {
            log.Write($"{Describe(type)}: TurboDL is not running, not starting it.");
            return Fail(NotRunning);
        }

        try
        {
            log.Write($"{type}: TurboDL is not running, starting it.");
            launcher.Launch();
        }
        catch (Exception ex)
        {
            log.Write($"Couldn't start TurboDL: {ex.Message}");
            return Fail($"Couldn't start TurboDL: {ex.Message}");
        }

        try
        {
            return Logged(type, await SendAsync(line, StartupTimeout, ct));
        }
        catch (IpcUnavailableException)
        {
            log.Write($"{type}: TurboDL didn't open its pipe within {StartupTimeout.TotalSeconds:0} s.");
            return Fail($"TurboDL didn't start within {StartupTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Write($"{type}: {ex.GetType().Name}: {ex.Message}");
            return Fail(ex.Message);
        }
    }

    private Task<string> SendAsync(string line, TimeSpan connectTimeout, CancellationToken ct) =>
        new IpcClient(PipeName) { ConnectTimeout = connectTimeout, ReplyTimeout = ReplyTimeout }.SendLineAsync(line, ct);

    /// <summary>
    /// The message's "type", and the message as one line. Chrome sends compact JSON, which is forwarded untouched;
    /// anything with line breaks in it is re-written compactly, since the pipe carries one message per line.
    /// </summary>
    private static (string Type, string Line) Inspect(string message)
    {
        using var doc = JsonDocument.Parse(message);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");

        var type = "";
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("type", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                type = property.Value.GetString() ?? "";
                break;
            }
        }

        if (message.AsSpan().IndexOfAny('\r', '\n') < 0) return (type, message);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            doc.WriteTo(writer);
        return (type, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    /// <summary>Logs the outcome. Only the type and ok/error: messages carry cookies, which never go in the log.</summary>
    private string Logged(string type, string reply)
    {
        string outcome;
        try
        {
            outcome = IpcJson.ParseReply(reply) is { Ok: false, Error: var error } ? $"refused: {error}" : "ok";
        }
        catch (JsonException)
        {
            outcome = "unreadable reply";
        }
        log.Write($"{Describe(type)}: {outcome}");
        return reply;
    }

    private async Task<bool> TryWriteAsync(Stream output, string reply, CancellationToken ct)
    {
        try
        {
            await NativeMessaging.WriteAsync(output, reply, ct);
            return true;
        }
        catch (IpcProtocolException ex)
        {
            // The reply was over Chrome's 1 MB limit; say so instead.
            log.Write($"Reply not sent: {ex.Message}");
            return await TryWriteAsync(output, Fail(ex.Message), ct);
        }
        catch (IOException ex)
        {
            log.Write($"Chrome went away: {ex.Message}");
            return false;
        }
    }

    private static string Describe(string type) => type.Length == 0 ? "(no type)" : type;

    private static string Fail(string error) => IpcJson.Serialize(IpcReply.Fail(error));
}
