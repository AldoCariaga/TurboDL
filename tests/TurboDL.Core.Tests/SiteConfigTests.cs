using TurboDL.Core.Sites;

namespace TurboDL.Core.Tests;

public sealed class SiteConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TurboDL.SiteConfigTests." + Guid.NewGuid().ToString("N"));

    public SiteConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Example = """
        {
          // comments and trailing commas are fine in a hand-written file
          "id": "example",
          "name": "Example Host",
          "hosts": ["example.com", "Files.Example.NET."],
          "browserFirst": true,
          "steps": [
            { "action": "waitFor", "selector": "#go", "state": "visible", "timeoutSeconds": 30 },
            { "action": "countdown", "selector": "#timer", "timeoutSeconds": 180 },
            { "action": "captcha" },
            { "action": "click", "selector": "#go", "optional": true },
            { "action": "wait", "seconds": 2.5 },
          ],
          "capture": { "urlPattern": "^https://dl\\.example\\.com/", "requireAttachment": false },
          "popups": "keep",
          "offSiteRedirects": "goBack",
          "whenStepsEnd": "showBrowser"
        }
        """;

    private string Write(string name, string json)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static string? Problem(string json) => SiteConfigLoader.Parse(json, SiteSource.User, "test.json").Problem;

    private static string Minimal(string steps = """[{ "action": "click", "selector": "#go" }]""", string extra = "") =>
        $$"""{ "id": "x", "name": "X", "hosts": ["x.com"], "steps": {{steps}} {{extra}} }""";

    [Fact]
    public void Parse_FullExample_ReadsEveryField()
    {
        var (config, problem) = SiteConfigLoader.Parse(Example, SiteSource.User, @"C:\sites\example.json");

        Assert.Null(problem);
        Assert.NotNull(config);
        Assert.Equal("example", config.Id);
        Assert.Equal("Example Host", config.Name);
        Assert.Equal(["example.com", "files.example.net"], config.Hosts);
        Assert.True(config.BrowserFirst);
        Assert.Equal(
            [SiteAction.WaitFor, SiteAction.Countdown, SiteAction.Captcha, SiteAction.Click, SiteAction.Wait],
            config.Steps.Select(s => s.Action));
        Assert.Equal(30, config.Steps[0].TimeoutSeconds);
        Assert.Equal(SiteConfigLoader.DefaultTimeoutSeconds, config.Steps[2].TimeoutSeconds);
        Assert.True(config.Steps[3].Optional);
        Assert.False(config.Steps[0].Optional);
        Assert.Null(config.Steps[2].Selector);
        Assert.Equal(2.5, config.Steps[4].Seconds);
        Assert.True(config.IsFileUrl("https://dl.example.com/abc/file.rar"));
        Assert.False(config.IsFileUrl("https://example.com/abc"));
        Assert.False(config.RequireAttachment);
        Assert.Equal(PopupHandling.Keep, config.Popups);
        Assert.Equal(OffSiteHandling.GoBack, config.OffSiteRedirects);
        Assert.Equal(StepsEnd.ShowBrowser, config.WhenStepsEnd);
        Assert.Equal(@"C:\sites\example.json", config.Origin);
    }

    [Fact]
    public void Parse_Defaults_WhenLeftOut()
    {
        var (config, _) = SiteConfigLoader.Parse(Minimal(), SiteSource.User, "x.json");

        Assert.NotNull(config);
        Assert.False(config.BrowserFirst);
        Assert.True(config.RequireAttachment);
        Assert.Null(config.CaptureUrl);
        Assert.Equal(PopupHandling.CloseAds, config.Popups);
        Assert.Equal(OffSiteHandling.Allow, config.OffSiteRedirects);
        Assert.Equal(StepsEnd.Generic, config.WhenStepsEnd);
        Assert.Equal(SiteConfigLoader.DefaultTimeoutSeconds, config.Steps[0].TimeoutSeconds);
    }

    [Theory]
    [InlineData("""{ "name": "X", "hosts": ["x.com"], "steps": [{ "action": "wait", "seconds": 1 }] }""", "\"id\" is missing")]
    [InlineData("""{ "id": "x", "hosts": ["x.com"], "steps": [{ "action": "wait", "seconds": 1 }] }""", "\"name\" is missing")]
    [InlineData("""{ "id": "x", "name": "X", "steps": [{ "action": "wait", "seconds": 1 }] }""", "\"hosts\" is missing")]
    [InlineData("""{ "id": "x", "name": "X", "hosts": ["x.com"] }""", "\"steps\" is missing")]
    [InlineData("""{ "id": "x", "name": "X", "hosts": ["*.x.com"], "steps": [{ "action": "wait", "seconds": 1 }] }""", "isn't a host name")]
    [InlineData("""{ "id": "x y", "name": "X", "hosts": ["x.com"], "steps": [{ "action": "wait", "seconds": 1 }] }""", "can only have")]
    public void Parse_MissingOrBadRequiredFields_AreRefused(string json, string expected) =>
        Assert.Contains(expected, Problem(json));

    [Fact]
    public void Parse_UnknownAction_IsRefused() =>
        Assert.Contains("\"clik\" isn't one TurboDL knows", Problem(Minimal("""[{ "action": "clik", "selector": "#go" }]""")));

    [Fact]
    public void Parse_UnknownProperty_IsRefused() =>
        Assert.Contains("\"script\" isn't a setting TurboDL knows", Problem(Minimal(extra: """, "script": "alert(1)" """)));

    [Fact]
    public void Parse_UnknownStepProperty_IsRefused() =>
        Assert.Contains("isn't a setting TurboDL knows", Problem(Minimal("""[{ "action": "click", "selector": "#go", "js": "x" }]""")));

    [Fact]
    public void Parse_PropertyNamesAreCaseSensitive() =>
        Assert.Contains("isn't a setting TurboDL knows", Problem(Minimal(extra: """, "BrowserFirst": true """)));

    [Theory]
    [InlineData("""[{ "action": "click" }]""", "needs a \"selector\"")]
    [InlineData("""[{ "action": "click", "selector": "  " }]""", "needs a \"selector\"")]
    [InlineData("""[{ "action": "captcha", "selector": "#c" }]""", "doesn't take a \"selector\"")]
    [InlineData("""[{ "action": "click", "selector": "#go", "state": "visible" }]""", "doesn't take a \"state\"")]
    [InlineData("""[{ "action": "waitFor", "selector": "#go", "state": "gone" }]""", "\"gone\" isn't one")]
    [InlineData("""[{ "action": "wait" }]""", "needs \"seconds\"")]
    [InlineData("""[{ "action": "wait", "seconds": 0 }]""", "more than 0")]
    [InlineData("""[{ "action": "click", "selector": "#go", "timeoutSeconds": 0 }]""", "between 1 and 600")]
    [InlineData("""[{ "action": "click", "selector": "#go", "timeoutSeconds": 601 }]""", "between 1 and 600")]
    [InlineData("""[{ "action": "click", "selector": "#go", "seconds": 3 }]""", "doesn't take \"seconds\"")]
    [InlineData("""[{ "action": "click", "selector": "#go", "timeoutSeconds": "ten" }]""", "wrong kind of value")]
    [InlineData("""[{ "action": "waitFor", "selector": "#go", "retryIfIgnoredSeconds": 4 }]""", "only click does")]
    [InlineData("""[{ "action": "wait", "seconds": 1, "retryIfIgnoredSeconds": 4 }]""", "only click does")]
    [InlineData("""[{ "action": "click", "selector": "#go", "retryIfIgnoredSeconds": 0 }]""", "between 1 and 60")]
    [InlineData("""[{ "action": "click", "selector": "#go", "retryIfIgnoredSeconds": 61 }]""", "between 1 and 60")]
    public void Parse_BadSteps_AreRefused(string steps, string expected) =>
        Assert.Contains(expected, Problem(Minimal(steps)));

    [Fact]
    public void Parse_ClickRetryIfIgnored_IsKept_AndOffByDefault()
    {
        var on = SiteConfigLoader.Parse(Minimal("""[{ "action": "click", "selector": "#go", "retryIfIgnoredSeconds": 4.5 }]"""), SiteSource.User, "t").Config!;
        var off = SiteConfigLoader.Parse(Minimal(), SiteSource.User, "t").Config!;

        Assert.Equal(4.5, Assert.Single(on.Steps).RetryIfIgnoredSeconds);
        Assert.Equal(0, Assert.Single(off.Steps).RetryIfIgnoredSeconds);
    }

    [Theory]
    [InlineData("https://cdn.files.example/x.rar", true)]
    [InlineData("https://eu.cdn.files.example/x.rar", true)]
    [InlineData("https://x.com/f/1", true)] // the config's own hosts
    [InlineData("https://www.x.com/f/1", true)]
    [InlineData("https://googleads.g.doubleclick.net/pagead/ads", false)]
    [InlineData("https://evilcdn.files.example/x.rar", false)]
    [InlineData("not a url", false)]
    public void FileHosts_LimitWhereTheFileMayComeFrom(string url, bool expected)
    {
        var config = SiteConfigLoader.Parse(Minimal(extra: """, "capture": { "fileHosts": ["CDN.files.example"] } """), SiteSource.User, "x").Config!;

        Assert.Equal(expected, config.IsFileHost(url));
    }

    [Fact]
    public void FileHosts_LeftOut_AnyHost() =>
        Assert.True(SiteConfigLoader.Parse(Minimal(), SiteSource.User, "x").Config!.IsFileHost("https://anything.example/f"));

    [Fact]
    public void FileHosts_Wildcard_IsRefused() =>
        Assert.Contains("in \"capture.fileHosts\" isn't a host name", Problem(Minimal(extra: """, "capture": { "fileHosts": ["*.cdn.example"] } """)));

    [Fact]
    public void Parse_UnknownOffSiteValue_IsRefused() =>
        Assert.Contains("\"stay\" isn't one TurboDL knows", Problem(Minimal(extra: """, "offSiteRedirects": "stay" """)));

    [Fact]
    public void Parse_BadRegex_IsRefused() =>
        Assert.Contains("isn't a valid regular expression", Problem(Minimal(extra: """, "capture": { "urlPattern": "([a-z" } """)));

    [Fact]
    public void Parse_BrokenJson_IsRefused() =>
        Assert.Contains("isn't valid JSON", Problem("""{ "id": "x", """));

    [Fact]
    public void Load_BuiltInFileKeeper_Loads()
    {
        var catalog = SiteConfigLoader.Load(null);

        var fileKeeper = Assert.Single(catalog.Sites, s => s.Id == "filekeeper");
        Assert.Equal(SiteSource.BuiltIn, fileKeeper.Source);
        Assert.Equal("FileKeeper", fileKeeper.Name);
        Assert.Equal(["filekeeper.net"], fileKeeper.Hosts);
        Assert.Equal(OffSiteHandling.GoBack, fileKeeper.OffSiteRedirects); // its download button can send the tab to ads
        Assert.True(fileKeeper.IsFileHost("https://edge01.fetchio.cc/f/abc"));
        Assert.False(fileKeeper.IsFileHost("https://googleads.g.doubleclick.net/pagead/ads")); // an ad script once taken for part01
        // An ad can take the click and only open a pop-up: the button is clicked again instead of waiting 20 s for nothing.
        Assert.True(Assert.Single(fileKeeper.Steps, s => s.Action == SiteAction.Click).RetryIfIgnoredSeconds > 0);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void Load_UserFileWithBuiltInId_ReplacesIt()
    {
        Write("mine.json", """{ "id": "filekeeper", "name": "My FileKeeper", "hosts": ["mine.example"], "steps": [{ "action": "wait", "seconds": 1 }] }""");

        var catalog = SiteConfigLoader.Load(_root);

        var fileKeeper = Assert.Single(catalog.Sites, s => s.Id == "filekeeper");
        Assert.Equal("My FileKeeper", fileKeeper.Name);
        Assert.Equal(SiteSource.User, fileKeeper.Source);
        Assert.True(fileKeeper.ReplacesBuiltIn);
        Assert.Equal(Path.Combine(_root, "mine.json"), fileKeeper.Origin);
    }

    [Fact]
    public void Load_BrokenFile_IsAProblem_AndTheOthersStillLoad()
    {
        var broken = Write("a-broken.json", "{ not json");
        Write("b-example.json", Example);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a config");

        var catalog = SiteConfigLoader.Load(_root);

        var problem = Assert.Single(catalog.Problems);
        Assert.Equal(broken, problem.File);
        Assert.Contains(catalog.Sites, s => s.Id == "example" && s.Source == SiteSource.User && !s.ReplacesBuiltIn);
        Assert.Contains(catalog.Sites, s => s.Id == "filekeeper" && s.Source == SiteSource.BuiltIn);
    }

    [Fact]
    public void Load_TwoUserFilesWithOneId_SecondIsAProblem()
    {
        Write("a.json", Example);
        var second = Write("b.json", Example.Replace("Example Host", "Second"));

        var catalog = SiteConfigLoader.Load(_root);

        Assert.Equal("Example Host", Assert.Single(catalog.Sites, s => s.Id == "example").Name);
        var problem = Assert.Single(catalog.Problems);
        Assert.Equal(second, problem.File);
        Assert.Contains("a.json already has the id", problem.Reason);
    }

    [Fact]
    public void Load_MissingUserFolder_IsJustTheBuiltIns()
    {
        var catalog = SiteConfigLoader.Load(Path.Combine(_root, "nope"));

        Assert.Empty(catalog.Problems);
        Assert.All(catalog.Sites, s => Assert.Equal(SiteSource.BuiltIn, s.Source));
    }

    [Theory]
    [InlineData("https://example.com/f/abc", "example")]
    [InlineData("https://EXAMPLE.com./f/abc", "example")]
    [InlineData("https://www.example.com/f/abc", "example")]
    [InlineData("https://a.b.files.example.net/x", "example")]
    [InlineData("https://evilexample.com/f/abc", null)]
    [InlineData("https://example.com.evil.org/f/abc", null)]
    [InlineData("https://example.net/x", null)]
    [InlineData("https://other.org/x", null)]
    [InlineData("ftp://example.com/x", null)]
    public void Match_ByHostLabel(string url, string? expected)
    {
        var (config, _) = SiteConfigLoader.Parse(Example, SiteSource.User, "example.json");
        var catalog = new SiteCatalog([config!], []);

        Assert.Equal(expected, catalog.Match(new Uri(url))?.Id);
    }

    [Fact]
    public void Match_MostSpecificHostWins()
    {
        var broad = SiteConfigLoader.Parse(Minimal().Replace("\"x\"", "\"broad\"").Replace("x.com", "host.com"), SiteSource.User, "a").Config!;
        var narrow = SiteConfigLoader.Parse(Minimal().Replace("\"x\"", "\"narrow\"").Replace("x.com", "dl.host.com"), SiteSource.User, "b").Config!;
        var catalog = new SiteCatalog([broad, narrow], []);

        Assert.Equal("narrow", catalog.Match(new Uri("https://eu.dl.host.com/f"))?.Id);
        Assert.Equal("broad", catalog.Match(new Uri("https://www.host.com/f"))?.Id);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("Wait 12 seconds", 12)]
    [InlineData("Your link is ready in 01:30", 90)]
    [InlineData("0", 0)]
    [InlineData("Download ready", null)]
    [InlineData("", null)]
    public void Countdown_SecondsLeft(string text, int? expected) => Assert.Equal(expected, SiteCountdown.SecondsLeft(text));
}
