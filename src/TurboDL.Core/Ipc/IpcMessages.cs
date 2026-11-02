using System.Text.Json.Serialization;

namespace TurboDL.Core.Ipc;

/// <summary>The commands the app understands on this user's <c>TurboDL.Ipc.&lt;SID&gt;</c> pipe (and the native host forwards from Chrome).</summary>
public static class IpcCommands
{
    public const string Add = "add";
    public const string Show = "show";
    public const string GetConfig = "getConfig";

    public static bool IsKnown(string? type) => type is Add or Show or GetConfig;
}

/// <summary>
/// One request line: <c>{"type":"add","url","filename?","referrer?","userAgent?","cookies?","size?"}</c>,
/// <c>{"type":"show"}</c> or <c>{"type":"getConfig"}</c>.
/// </summary>
public sealed class IpcRequest
{
    public string Type { get; set; } = "";

    public string? Url { get; set; }

    /// <summary>Suggested file name (the browser's pick). Empty or missing = take it from the server.</summary>
    public string? Filename { get; set; }

    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>The value of a Cookie header, e.g. <c>a=1; b=2</c>.</summary>
    public string? Cookies { get; set; }

    /// <summary>Size the browser expects, in bytes, if it knows. Informational only: the app probes the real size.</summary>
    public long? Size { get; set; }

    public static IpcRequest Show() => new() { Type = IpcCommands.Show };

    public static IpcRequest GetConfig() => new() { Type = IpcCommands.GetConfig };

    public static IpcRequest Add(string url, string? filename = null, string? referrer = null, string? userAgent = null,
        string? cookies = null, long? size = null) =>
        new()
        {
            Type = IpcCommands.Add,
            Url = url,
            Filename = filename,
            Referrer = referrer,
            UserAgent = userAgent,
            Cookies = cookies,
            Size = size,
        };

    /// <summary>The request headers a caught browser download should carry: Cookie, Referer and User-Agent, when given.</summary>
    public Dictionary<string, string> BrowserHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Cookies)) headers["Cookie"] = Cookies.Trim();
        if (!string.IsNullOrWhiteSpace(Referrer)) headers["Referer"] = Referrer.Trim();
        if (!string.IsNullOrWhiteSpace(UserAgent)) headers["User-Agent"] = UserAgent.Trim();
        return headers;
    }

    /// <summary>Why this request can't be carried out, or null when it's well formed.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Type)) return "The request has no \"type\".";
        if (!IpcCommands.IsKnown(Type)) return $"Unknown command \"{Type}\".";
        if (Type == IpcCommands.Add)
        {
            if (string.IsNullOrWhiteSpace(Url)) return "\"add\" needs a \"url\".";
            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "\"url\" must be an http:// or https:// link.";
        }
        return null;
    }
}

/// <summary>Every reply is <c>{"ok":true|false,"error?"}</c>; <c>getConfig</c> adds the catching settings.</summary>
public sealed class IpcReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>getConfig only: file extensions (lower case, no dot) the browser should hand over.</summary>
    public string[]? CatchExtensions { get; set; }

    /// <summary>getConfig only: browser downloads smaller than this stay in the browser. 0 = catch everything.</summary>
    [JsonPropertyName("minCatchSizeKB")]
    public long? MinCatchSizeKB { get; set; }

    public static IpcReply Success() => new() { Ok = true };

    public static IpcReply Fail(string error) => new() { Ok = false, Error = error };

    public static IpcReply Config(string[] catchExtensions, long minCatchSizeKB) =>
        new() { Ok = true, CatchExtensions = catchExtensions, MinCatchSizeKB = minCatchSizeKB };
}
