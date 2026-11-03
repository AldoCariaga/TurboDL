using System.Text;
using TurboDL.Core.Archives;

namespace TurboDL.Core.Tests;

public sealed class FileSnifferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TurboDL.FileSnifferTests." + Guid.NewGuid().ToString("N"));

    public FileSnifferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] WithTail(params byte[] head) => [.. head, .. Enumerable.Repeat((byte)0x41, 600)];

    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, FileKind.Rar4)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, FileKind.Rar5)]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, FileKind.SevenZip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, FileKind.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, FileKind.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x07, 0x08 }, FileKind.Zip)]
    public void Signatures(byte[] head, FileKind expected)
    {
        Assert.Equal(expected, FileSniffer.Sniff(head));
        Assert.Equal(expected, FileSniffer.Sniff(WithTail(head)));
    }

    [Fact]
    public void Html_WithBomAndLeadingNewlines()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("\r\n\n  \t<html><head><title>Sign in</title>")];
        Assert.Equal(FileKind.Html, FileSniffer.Sniff(bytes));
    }

    [Fact]
    public void Html_UpperCaseDoctype()
    {
        Assert.Equal(FileKind.Html, FileSniffer.Sniff(Encoding.ASCII.GetBytes("<!DOCTYPE html>\n<HTML lang=\"en\">")));
    }

    [Theory]
    [InlineData("<head><meta charset=utf-8>")]
    [InlineData("<BODY>")]
    [InlineData("<script>location='/login'</script>")]
    [InlineData("<meta http-equiv=refresh content=0>")]
    [InlineData("<title>Error</title>")]
    [InlineData("<!-- served by nginx -->")]
    [InlineData("<?xml version=\"1.0\"?>\n<!DOCTYPE x>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">")]
    public void Html_OtherStarts(string text)
    {
        Assert.Equal(FileKind.Html, FileSniffer.Sniff(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void XmlWithoutHtml_IsUnknown()
    {
        Assert.Equal(FileKind.Unknown, FileSniffer.Sniff(Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code></Error>")));
    }

    [Fact]
    public void Json_IsUnknown()
    {
        Assert.Equal(FileKind.Unknown, FileSniffer.Sniff(Encoding.ASCII.GetBytes("{\"error\":\"not found\",\"html\":\"<html>\"}")));
    }

    [Fact]
    public void Empty_IsUnknown()
    {
        Assert.Equal(FileKind.Unknown, FileSniffer.Sniff([]));
    }

    [Theory]
    [InlineData("x.part02.rar", FileKind.Html, false)]
    [InlineData("x.part02.rar", FileKind.Rar5, true)]
    [InlineData("x.rar", FileKind.Rar4, true)]
    [InlineData("x.rar", FileKind.Zip, false)]
    [InlineData("x.7z.002", FileKind.Unknown, true)]
    [InlineData("x.7z.001", FileKind.SevenZip, true)]
    [InlineData("x.7z.001", FileKind.Html, false)]
    [InlineData("x.7z", FileKind.Unknown, false)]
    [InlineData("x.zip.001", FileKind.Zip, true)]
    [InlineData("x.zip.001", FileKind.Html, false)]
    [InlineData("x.zip.003", FileKind.Unknown, true)]
    [InlineData("x.zip", FileKind.Html, false)]
    [InlineData("x.txt", FileKind.Html, true)]
    [InlineData("x.mkv", FileKind.Unknown, true)]
    public void MatchesName(string fileName, FileKind kind, bool expected)
    {
        Assert.Equal(expected, FileSniffer.MatchesName(fileName, kind));
    }

    [Fact]
    public void SniffFile_ReadsTheHead()
    {
        var path = Path.Combine(_root, "a.part01.rar");
        File.WriteAllBytes(path, WithTail(0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00));
        Assert.Equal(FileKind.Rar5, FileSniffer.SniffFile(path));

        var page = Path.Combine(_root, "b.part02.rar");
        File.WriteAllText(page, "<!doctype html><title>Sign in</title>");
        Assert.Equal(FileKind.Html, FileSniffer.SniffFile(page));
    }

    [Fact]
    public void SniffFile_MissingPath_IsUnknown()
    {
        Assert.Equal(FileKind.Unknown, FileSniffer.SniffFile(Path.Combine(_root, "nope", "missing.rar")));
    }
}
