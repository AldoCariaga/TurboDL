// A stand-in for yt-dlp.exe. The URL's path picks the scenario; "?log=<file>" makes it record its
// arguments (and the cookies file it was given) as JSON, so tests can check exactly what was passed.
// It evaluates TurboDL's --progress-template and --print templates the way yt-dlp does: missing fields
// become "NA", and %(...)j fields are JSON.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
var stdout = Console.Out;
var stderr = Console.Error;

if (args.Contains("--child-sleep"))
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

if (args.Contains("-U"))
{
    stdout.WriteLine("Current version: stable@2026.01.01 from yt-dlp/yt-dlp");
    stdout.WriteLine("yt-dlp is up to date (stable@2026.01.01 from yt-dlp/yt-dlp)");
    return 0;
}

var dash = Array.IndexOf(args, "--");
if (dash < 0 || dash == args.Length - 1)
{
    stderr.WriteLine("Usage: yt-dlp [OPTIONS] URL [URL...]");
    stderr.WriteLine("yt-dlp: error: You must provide at least one URL.");
    return 2;
}
var url = new Uri(args[dash + 1]);
var query = System.Web.HttpUtility.ParseQueryString(url.Query);
var log = query["log"];
var probe = args.Contains("-J");

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
}

int? childPid = null;
if (url.AbsolutePath is "/slow" or "/hang")
{
    // A grandchild, like the ffmpeg yt-dlp starts: killing the tree must take it down too.
    var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child-sleep") { UseShellExecute = false, CreateNoWindow = true })!;
    childPid = child.Id;
}

if (log is not null)
{
    var cookiesPath = Option("--cookies");
    File.WriteAllText(log, JsonSerializer.Serialize(new
    {
        args,
        cookiesPath,
        cookies = cookiesPath is not null && File.Exists(cookiesPath) ? File.ReadAllText(cookiesPath) : null,
        pid = Environment.ProcessId,
        childPid,
    }));
}

switch (url.AbsolutePath)
{
    case "/unsupported":
        stderr.WriteLine($"ERROR: Unsupported URL: {url}");
        return 1;
    case "/geo":
        stderr.WriteLine("WARNING: [youtube] Falling back to generic n function search");
        stderr.WriteLine("ERROR: [youtube] abc123: The uploader has not made this video available in your country");
        return 1;
    case "/fail":
        stderr.WriteLine("ERROR: [generic] Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)");
        return 1;
    case "/hang":
        Thread.Sleep(Timeout.Infinite);
        return 0;
}

if (probe)
{
    switch (url.AbsolutePath)
    {
        case "/video":
            stdout.WriteLine(VideoJson(url));
            return 0;
        case "/playlist":
            stdout.WriteLine("""{"_type":"playlist","id":"PL1","title":"A list","entries":[]}""");
            return 0;
    }
    stderr.WriteLine($"ERROR: Unsupported URL: {url}");
    return 1;
}

var progressTemplate = Templates("--progress-template", "download:");
var ppTemplate = Templates("--progress-template", "postprocess:");
var printTemplate = Templates("--print", "after_move:");

void Progress(string format, string vcodec, string acodec, long done, long? total, long? estimate, double speed, double eta)
{
    if (progressTemplate is null) return;
    stdout.WriteLine(Evaluate(progressTemplate, new()
    {
        ["progress.status"] = "downloading",
        ["progress.downloaded_bytes"] = done,
        ["progress.total_bytes"] = total,
        ["progress.total_bytes_estimate"] = estimate,
        ["progress.speed"] = speed,
        ["progress.eta"] = eta,
        ["info.format_id"] = format,
        ["info.vcodec"] = vcodec,
        ["info.acodec"] = acodec,
    }));
}

if (url.AbsolutePath == "/slow")
{
    for (long i = 1; ; i++)
    {
        Progress("18", "avc1.42001E", "mp4a.40.2", i * 1000, 10_000_000, null, 1000, 9999);
        Thread.Sleep(100);
    }
}

if (url.AbsolutePath != "/video")
{
    stderr.WriteLine($"ERROR: Unsupported URL: {url}");
    return 1;
}

// Video stream, then audio stream (size only estimated), then the merge, like a YouTube 1080p download.
Progress("137", "avc1.640028", "none", 0, 3000, null, 0, 0);
Progress("137", "avc1.640028", "none", 1500, 3000, null, 1500, 1);
Progress("137", "avc1.640028", "none", 3000, 3000, null, 1500, 0);
Progress("140", "none", "mp4a.40.2", 500, null, 1000, 500, 1);
Progress("140", "none", "mp4a.40.2", 1000, null, 1000, 500, 0);
if (ppTemplate is not null)
{
    stdout.WriteLine(Evaluate(ppTemplate, new() { ["progress.status"] = "started", ["progress.postprocessor"] = "Merger" }));
    stdout.WriteLine(Evaluate(ppTemplate, new() { ["progress.status"] = "finished", ["progress.postprocessor"] = "Merger" }));
}

var dir = Option("-P") ?? Environment.CurrentDirectory;
var name = Evaluate(Option("-o") ?? "%(title)s.%(ext)s", new() { ["title"] = "Fäke vidéo ✓", ["ext"] = "mp4" });
var path = Path.Combine(dir, name);
File.WriteAllText(path, "fake video");
if (printTemplate is not null) stdout.WriteLine(Evaluate(printTemplate, new() { ["filepath"] = path }));
return 0;

string? Templates(string option, string when)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == option && args[i + 1].StartsWith(when, StringComparison.Ordinal))
            return args[i + 1][when.Length..];
    return null;
}

static string Evaluate(string template, Dictionary<string, object?> values)
{
    var result = Regex.Replace(template, @"%\(([\w.]+)\)(?:\.\d+)?([sjB])", m =>
    {
        values.TryGetValue(m.Groups[1].Value, out var value);
        value ??= "NA";
        return m.Groups[2].Value == "j" ? JsonSerializer.Serialize(value) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
    });
    return result.Replace("%%", "%");
}

static string VideoJson(Uri url) => JsonSerializer.Serialize(new Dictionary<string, object?>
{
    ["id"] = "abc123",
    ["title"] = "Fäke vidéo ✓ 動画",
    ["thumbnail"] = "https://i.example/abc123.jpg",
    ["duration"] = 212.5,
    ["extractor"] = "youtube",
    ["extractor_key"] = "Youtube",
    ["uploader"] = "Someone",
    ["webpage_url"] = url.GetLeftPart(UriPartial.Path),
    ["formats"] = new object[]
    {
        new Dictionary<string, object?> { ["format_id"] = "sb0", ["ext"] = "mhtml", ["vcodec"] = "none", ["acodec"] = "none", ["format_note"] = "storyboard" },
        new Dictionary<string, object?> { ["format_id"] = "140", ["ext"] = "m4a", ["vcodec"] = "none", ["acodec"] = "mp4a.40.2", ["resolution"] = "audio only", ["filesize"] = 3_400_000, ["tbr"] = 129.5, ["protocol"] = "https" },
        new Dictionary<string, object?> { ["format_id"] = "18", ["ext"] = "mp4", ["vcodec"] = "avc1.42001E", ["acodec"] = "mp4a.40.2", ["width"] = 640, ["height"] = 360, ["resolution"] = "640x360", ["fps"] = 25, ["filesize_approx"] = 9_000_000, ["format_note"] = "360p" },
        new Dictionary<string, object?> { ["format_id"] = "137", ["ext"] = "mp4", ["vcodec"] = "avc1.640028", ["acodec"] = "none", ["width"] = 1920, ["height"] = 1080, ["resolution"] = "1920x1080", ["fps"] = 29.97, ["filesize"] = 52_000_000, ["format_note"] = "1080p" },
        new Dictionary<string, object?> { ["format_id"] = "hls-x", ["ext"] = "mp4", ["width"] = null, ["height"] = null, ["protocol"] = "m3u8_native" },
    },
});
