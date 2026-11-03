using TurboDL.Core.Catching;

namespace TurboDL.Core.Tests;

public sealed class UrlExtractorTests
{
    private static string[] Extract(string? text) => [.. UrlExtractor.ExtractAll(text).Select(u => u.AbsoluteUri)];

    [Fact]
    public void MarkdownLink_GivesOneUrl()
    {
        Assert.Equal(["https://example.com/data.part01.rar"],
            Extract("[https://example.com/data.part01.rar](https://example.com/data.part01.rar)"));
    }

    [Fact]
    public void LinksRunTogether_AreSplit_InOrder()
    {
        Assert.Equal(
            ["http://localhost:8080/testdata.part01.rar", "http://localhost:8080/testdata.part02.rar", "http://localhost:8080/testdata.part03.rar"],
            Extract("http://localhost:8080/testdata.part01.rarhttp://localhost:8080/testdata.part02.rarhttp://localhost:8080/testdata.part03.rar"));
    }

    [Fact]
    public void TwoRunTogether_ThenANewline_ThenAThird_GivesThree()
    {
        Assert.Equal(["https://a.com/1.rar", "https://a.com/2.rar", "https://a.com/3.rar"],
            Extract("https://a.com/1.rarhttps://a.com/2.rar\nhttps://a.com/3.rar"));
    }

    [Fact]
    public void UpperCaseSchemeInTheMiddle_StillSplits()
    {
        Assert.Equal(["https://a.com/x.rar", "https://b.com/y.rar"], Extract("https://a.com/x.rarHTTPS://b.com/y.rar"));
    }

    [Fact]
    public void UrlInAQueryString_IsSplit_TheDocumentedTradeOff()
    {
        Assert.Equal(["https://a.com/go?u=", "http://b.com/x"], Extract("https://a.com/go?u=http://b.com/x"));
    }

    [Fact]
    public void FiftyLines_WithCrLfAndBlankLines_GiveFifty_InOrder()
    {
        var expected = Enumerable.Range(1, 50).Select(i => $"https://h/data.part{i:00}.rar").ToArray();
        var text = "\r\n" + string.Join("\r\n", expected.Select((u, i) => i % 7 == 0 ? u + "\r\n\r\n" : u)) + "\r\n\r\n";

        Assert.Equal(expected, Extract(text));
    }

    [Fact]
    public void TrailingPunctuation_IsDropped()
    {
        Assert.Equal(["https://a.com/x.rar", "https://a.com/y.rar"], Extract("\"see https://a.com/x.rar, and https://a.com/y.rar.\""));
    }

    [Fact]
    public void UnmatchedClosingParen_IsDropped_MatchedOneKept()
    {
        Assert.Equal(["https://a.com/x.rar"], Extract("(https://a.com/x.rar)"));
        Assert.Equal(["https://en.wikipedia.org/wiki/Foo_(bar)"], Extract("https://en.wikipedia.org/wiki/Foo_(bar)"));
    }

    [Theory]
    [InlineData("ftp://a.com/x.rar")]
    [InlineData("mailto:someone@a.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://")]
    [InlineData("http:///x.rar")]
    [InlineData("just some plain words")]
    public void NonHttpOrHostless_GivesNothing(string text)
    {
        Assert.Empty(Extract(text));
    }

    [Fact]
    public void UpperCaseUrl_IsAccepted()
    {
        var all = UrlExtractor.ExtractAll("HTTPS://A.COM/X.RAR");
        var uri = Assert.Single(all);
        Assert.Equal("a.com", uri.Host);
        Assert.Equal("/X.RAR", uri.AbsolutePath);
    }

    [Fact]
    public void OverMaxLength_IsSkipped()
    {
        var longUrl = "https://a.com/" + new string('x', UrlExtractor.MaxUrlLength);
        Assert.Equal(["https://a.com/ok.rar"], Extract(longUrl + "\nhttps://a.com/ok.rar"));
    }

    [Fact]
    public void Duplicates_ComeBackOnce_AtTheFirstPosition()
    {
        Assert.Equal(["https://a.com/1.rar", "https://a.com/2.rar"],
            Extract("https://a.com/1.rar https://a.com/2.rar https://a.com/1.rar https://a.com/2.rar"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void NullOrBlank_GivesEmpty(string? text)
    {
        Assert.Empty(UrlExtractor.ExtractAll(text));
    }
}
