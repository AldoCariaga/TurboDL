using System.Text;

namespace TurboDL.Core.Ipc;

/// <summary>Writes one UTF-8 line (the text plus <c>\n</c>) and flushes.</summary>
public static class IpcLines
{
    /// <summary>Longest line either side accepts. Cookies make <c>add</c> the largest message; this is far above that.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct = default)
    {
        if (line.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("A line can't contain a line break.", nameof(line));
        var bytes = new byte[Utf8.GetByteCount(line) + 1];
        Utf8.GetBytes(line, bytes);
        bytes[^1] = (byte)'\n';
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>Reads <c>\n</c>-terminated UTF-8 lines from a stream, refusing lines longer than a limit.</summary>
public sealed class IpcLineReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    /// <summary>
    /// The next line without its terminator (a trailing <c>\r</c> is dropped too), or null at end of stream.
    /// A final line with no terminator is still returned.
    /// </summary>
    public async Task<string?> ReadLineAsync(int maxBytes = IpcLines.MaxLineBytes, CancellationToken ct = default)
    {
        var line = new MemoryStream();
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var take = (newline >= 0 ? newline : _end) - _start;
            if (line.Length + take > maxBytes)
                throw new IpcProtocolException($"Line is longer than {maxBytes} bytes.");
            line.Write(_buffer, _start, take);

            if (newline >= 0)
            {
                _start = newline + 1;
                return Decode(line);
            }

            _start = _end = 0;
            var read = await stream.ReadAsync(_buffer, ct);
            if (read == 0) return line.Length == 0 ? null : Decode(line);
            _end = read;
        }
    }

    private static string Decode(MemoryStream line)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
        if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
        try
        {
            return IpcLines.Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new IpcProtocolException("Line is not valid UTF-8.");
        }
    }
}
