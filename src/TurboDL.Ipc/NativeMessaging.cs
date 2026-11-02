using System.Buffers.Binary;
using System.Text;

namespace TurboDL.Core.Ipc;

/// <summary>
/// Chrome's native messaging framing: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
/// See https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging.
/// </summary>
public static class NativeMessaging
{
    /// <summary>Chrome rejects a message from the host larger than 1 MB.</summary>
    public const int MaxOutgoingBytes = 1024 * 1024;

    /// <summary>Chrome sends at most 64 MiB to the host; a bigger length means a corrupt stream.</summary>
    public const int MaxIncomingBytes = 64 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Frames a message. Throws <see cref="IpcProtocolException"/> if it's over Chrome's 1 MB limit.</summary>
    public static byte[] Encode(string json)
    {
        var length = Utf8.GetByteCount(json);
        if (length > MaxOutgoingBytes)
            throw new IpcProtocolException($"Message is {length} bytes; Chrome accepts at most {MaxOutgoingBytes}.");
        var frame = new byte[4 + length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        Utf8.GetBytes(json, frame.AsSpan(4));
        return frame;
    }

    /// <summary>Writes one framed message and flushes. Nothing is written if the message is too large.</summary>
    public static async Task WriteAsync(Stream output, string json, CancellationToken ct = default)
    {
        var frame = Encode(json);
        await output.WriteAsync(frame, ct);
        await output.FlushAsync(ct);
    }

    /// <summary>
    /// Reads one framed message. Returns null if the stream ends cleanly before a new message (Chrome closed the port).
    /// Throws <see cref="IpcProtocolException"/> on a truncated frame, an absurd length or invalid UTF-8.
    /// </summary>
    public static async Task<string?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        var header = new byte[4];
        var got = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (got == 0) return null;
        if (got < header.Length) throw new IpcProtocolException("The stream ended inside a message header.");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxIncomingBytes)
            throw new IpcProtocolException($"Message length {length} is out of range.");

        var body = new byte[length];
        got = await input.ReadAtLeastAsync(body, length, throwOnEndOfStream: false, ct);
        if (got < length) throw new IpcProtocolException($"The stream ended after {got} of {length} message bytes.");

        try
        {
            return Utf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new IpcProtocolException("Message is not valid UTF-8.");
        }
    }
}
