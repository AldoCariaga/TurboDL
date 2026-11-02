namespace TurboDL.Core.Ipc;

/// <summary>The other side broke the wire format: a line or message too long, invalid UTF-8, a truncated frame.</summary>
public sealed class IpcProtocolException(string message) : IOException(message);

/// <summary>Nothing is serving the pipe: TurboDL isn't running (or hasn't opened its pipe yet).</summary>
public sealed class IpcUnavailableException(string pipeName)
    : TimeoutException("TurboDL is not running.")
{
    public string PipeName { get; } = pipeName;
}
