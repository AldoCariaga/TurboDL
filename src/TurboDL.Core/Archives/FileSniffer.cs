using System.Text;

namespace TurboDL.Core.Archives;

/// <summary>What the first bytes of a file say it is.</summary>
public enum FileKind { Unknown, Rar4, Rar5, SevenZip, Zip, Html }

/// <summary>
/// Tells archives from web pages by their first bytes, so a sign-in or error page saved under an archive's name is
/// caught before 7-Zip sees it. Every RAR volume starts with the signature; only the first piece of a split 7z or
/// zip does.
/// </summary>
public static class FileSniffer
{
    /// <summary>How many bytes from the start of a file are looked at.</summary>
    public const int HeadSize = 512;

    private static readonly byte[] Rar4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static readonly byte[] SevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[][] Zip = [[0x50, 0x4B, 0x03, 0x04], [0x50, 0x4B, 0x05, 0x06], [0x50, 0x4B, 0x07, 0x08]];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly string[] HtmlStarts =
        ["<!doctype html", "<html", "<head", "<body", "<script", "<meta", "<title", "<!--"];

    /// <summary>The kind of file that starts with <paramref name="head"/>; only the first <see cref="HeadSize"/> bytes count.</summary>
    public static FileKind Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length > HeadSize) head = head[..HeadSize];

        if (head.StartsWith(Rar5)) return FileKind.Rar5;
        if (head.StartsWith(Rar4)) return FileKind.Rar4;
        if (head.StartsWith(SevenZip)) return FileKind.SevenZip;
        foreach (var zip in Zip)
            if (head.StartsWith(zip)) return FileKind.Zip;
        return IsHtml(head) ? FileKind.Html : FileKind.Unknown;
    }

    /// <summary>Reads up to <see cref="HeadSize"/> bytes; Unknown if the file can't be read.</summary>
    public static FileKind SniffFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadSize];
            var read = stream.ReadAtLeast(buffer, HeadSize, throwOnEndOfStream: false);
            return Sniff(buffer.AsSpan(0, read));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FileKind.Unknown;
        }
    }

    /// <summary>
    /// False when the name promises an archive the bytes don't match: .rar needs Rar4/Rar5; .zip and .zip.001 need Zip;
    /// .7z and .7z.001 need SevenZip. .7z.002 / .zip.002 and later have no signature, so they always match.
    /// Names that aren't archives always match.
    /// </summary>
    public static bool MatchesName(string fileName, FileKind kind)
    {
        if (!MultipartArchive.TryParse(fileName, out _, out var scheme, out var ext, out var number)) return true;
        if (scheme == VolumeScheme.Split && number > 1) return true;
        return ext switch
        {
            "rar" => kind is FileKind.Rar4 or FileKind.Rar5,
            "zip" => kind == FileKind.Zip,
            _ => kind == FileKind.SevenZip,
        };
    }

    private static bool IsHtml(ReadOnlySpan<byte> head)
    {
        var rest = head.StartsWith(Utf8Bom) ? head[Utf8Bom.Length..] : head;
        var i = 0;
        while (i < rest.Length && rest[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f') i++;
        rest = rest[i..];

        var start = Lower(rest[..Math.Min(64, rest.Length)]);
        foreach (var tag in HtmlStarts)
            if (start.StartsWith(tag, StringComparison.Ordinal)) return true;
        return start.StartsWith("<?xml", StringComparison.Ordinal) && Lower(head).Contains("<html", StringComparison.Ordinal);
    }

    private static string Lower(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append(b is >= (byte)'A' and <= (byte)'Z' ? (char)(b + 32) : (char)b);
        return sb.ToString();
    }
}
