using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TurboDL.Core.Sites;

/// <summary>
/// Reads site configs: the built-in ones (JSON resources in this assembly, under Sites/BuiltIn) and then <c>*.json</c>
/// in the user's sites folder, where a file with a built-in's id replaces it. A file that can't be used is skipped and
/// recorded in <see cref="SiteCatalog.Problems"/>; it never stops the others from loading.
/// <code>
/// {
///   "id": "example", "name": "Example", "hosts": ["example.com"], "browserFirst": true,
///   "steps": [
///     { "action": "waitFor", "selector": "#go", "state": "visible", "timeoutSeconds": 60 },
///     { "action": "countdown", "selector": "#timer", "timeoutSeconds": 180 },
///     { "action": "captcha" },
///     { "action": "click", "selector": "#go", "optional": false },
///     { "action": "wait", "seconds": 2 }
///   ],
///   "capture": { "urlPattern": null, "requireAttachment": true, "fileHosts": ["cdn.example.net"] },
///   "popups": "closeAds", "offSiteRedirects": "goBack", "whenStepsEnd": "generic"
/// }
/// </code>
/// Property names are exact and unknown ones are refused, so a typo is reported instead of quietly ignored.
/// </summary>
public static class SiteConfigLoader
{
    public const int DefaultTimeoutSeconds = 60;
    public const int MaxSeconds = 600;

    /// <summary>A bigger file isn't a site config.</summary>
    private const long MaxFileBytes = 256 * 1024;

    private const string ResourcePrefix = "TurboDL.Core.Sites.BuiltIn.";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>The built-in configs, then the user's (<paramref name="userDirectory"/> may be null or missing: none).</summary>
    public static SiteCatalog Load(string? userDirectory)
    {
        var problems = new List<SiteConfigProblem>();
        var byId = new Dictionary<string, SiteConfig>(StringComparer.Ordinal);

        var assembly = typeof(SiteConfigLoader).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var file = resource[ResourcePrefix.Length..];
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            Add(Parse(reader.ReadToEnd(), SiteSource.BuiltIn, file), file, builtIn: true);
        }

        if (!string.IsNullOrWhiteSpace(userDirectory) && Directory.Exists(userDirectory))
        {
            IEnumerable<string> files;
            try { files = [.. Directory.EnumerateFiles(userDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase)]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new SiteConfigProblem(userDirectory, $"The folder can't be read: {ex.Message}"));
                files = [];
            }
            var userIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in files)
            {
                (SiteConfig? Config, string? Problem) result;
                try
                {
                    result = new FileInfo(path).Length > MaxFileBytes
                        ? (null, $"It's over {MaxFileBytes / 1024} KB, which is too big for a site config.")
                        : Parse(File.ReadAllText(path), SiteSource.User, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result = (null, $"It can't be read: {ex.Message}");
                }
                if (result.Config is { } config && userIds.TryGetValue(config.Id, out var earlier))
                {
                    result = (null, $"{Path.GetFileName(earlier)} already has the id \"{config.Id}\", so this one is skipped.");
                }
                if (result.Config is { } ok)
                {
                    userIds[ok.Id] = path;
                    if (byId.TryGetValue(ok.Id, out var replaced) && replaced.Source == SiteSource.BuiltIn)
                        result = (ok with { ReplacesBuiltIn = true }, null);
                }
                Add(result, path, builtIn: false);
            }
        }

        return new SiteCatalog([.. byId.Values], problems);

        void Add((SiteConfig? Config, string? Problem) result, string file, bool builtIn)
        {
            if (result.Config is { } config)
            {
                if (builtIn && byId.ContainsKey(config.Id))
                {
                    problems.Add(new SiteConfigProblem(file, $"Another built-in config already has the id \"{config.Id}\"."));
                    return;
                }
                byId[config.Id] = config;
            }
            else
            {
                problems.Add(new SiteConfigProblem(file, result.Problem!));
            }
        }
    }

    /// <summary>One config from its JSON text, or why it can't be used (one sentence). <paramref name="origin"/> is kept on the config.</summary>
    public static (SiteConfig? Config, string? Problem) Parse(string json, SiteSource source, string origin)
    {
        ConfigJson? raw;
        try
        {
            raw = JsonSerializer.Deserialize<ConfigJson>(json, Options);
        }
        catch (JsonException ex)
        {
            return (null, Explain(ex));
        }
        if (raw is null) return (null, "The file is empty.");

        try
        {
            return (Build(raw, source, origin), null);
        }
        catch (InvalidConfigException ex)
        {
            return (null, ex.Message);
        }
    }

    private static string Explain(JsonException ex)
    {
        var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
        if (ex.Message.Contains("could not be mapped", StringComparison.Ordinal))
            return $"\"{ex.Path?.TrimStart('$', '.')}\" isn't a setting TurboDL knows{where}. Names are case-sensitive.";
        if (ex.Message.Contains("could not be converted", StringComparison.Ordinal))
            return $"\"{ex.Path?.TrimStart('$', '.')}\" has the wrong kind of value{where}.";
        return $"It isn't valid JSON{where}.";
    }

    private static SiteConfig Build(ConfigJson raw, SiteSource source, string origin)
    {
        var id = raw.Id?.Trim().ToLowerInvariant() ?? "";
        if (id.Length == 0) throw new InvalidConfigException("\"id\" is missing.");
        if (!IdPattern.IsMatch(id))
            throw new InvalidConfigException($"The id \"{raw.Id}\" can only have letters, digits, dots, dashes and underscores.");
        var name = raw.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidConfigException("\"name\" is missing.");

        if (raw.Hosts is not { Count: > 0 }) throw new InvalidConfigException("\"hosts\" is missing or empty.");
        var hosts = HostNames(raw.Hosts, "hosts");
        var fileHosts = raw.Capture?.FileHosts is { } listed ? HostNames(listed, "capture.fileHosts") : [];

        if (raw.Steps is not { Count: > 0 }) throw new InvalidConfigException("\"steps\" is missing or empty.");
        var steps = new List<SiteStep>();
        for (var i = 0; i < raw.Steps.Count; i++) steps.Add(BuildStep(raw.Steps[i], i + 1));

        Regex? captureUrl = null;
        if (raw.Capture?.UrlPattern is { } pattern)
        {
            if (pattern.Length == 0) throw new InvalidConfigException("\"capture.urlPattern\" is empty; leave it out or use null.");
            try
            {
                captureUrl = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidConfigException($"\"capture.urlPattern\" isn't a valid regular expression: {ex.Message}");
            }
        }

        return new SiteConfig
        {
            Id = id,
            Name = name,
            Hosts = hosts,
            BrowserFirst = raw.BrowserFirst ?? false,
            Steps = steps,
            CaptureUrl = captureUrl,
            FileHosts = fileHosts,
            RequireAttachment = raw.Capture?.RequireAttachment ?? true,
            Popups = Choose(raw.Popups, "popups", PopupHandling.CloseAds, ("closeAds", PopupHandling.CloseAds), ("keep", PopupHandling.Keep)),
            OffSiteRedirects = Choose(raw.OffSiteRedirects, "offSiteRedirects", OffSiteHandling.Allow,
                ("allow", OffSiteHandling.Allow), ("goBack", OffSiteHandling.GoBack)),
            WhenStepsEnd = Choose(raw.WhenStepsEnd, "whenStepsEnd", StepsEnd.Generic,
                ("generic", StepsEnd.Generic), ("showBrowser", StepsEnd.ShowBrowser)),
            Source = source,
            Origin = origin,
        };
    }

    /// <summary>Lower-case host names, each checked; a wildcard is refused (subdomains match by themselves).</summary>
    private static List<string> HostNames(IEnumerable<string?> raw, string field)
    {
        var hosts = new List<string>();
        foreach (var rawHost in raw)
        {
            var host = rawHost?.Trim().TrimEnd('.').ToLowerInvariant() ?? "";
            if (host.Length == 0 || host.Contains('*') || Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw new InvalidConfigException($"\"{rawHost}\" in \"{field}\" isn't a host name like \"example.com\" (its subdomains match by themselves).");
            if (!hosts.Contains(host)) hosts.Add(host);
        }
        return hosts;
    }

    private static SiteStep BuildStep(StepJson? raw, int number)
    {
        var at = $"Step {number}";
        if (raw is null) throw new InvalidConfigException($"{at} is empty.");
        var action = Choose(raw.Action, $"{at}'s \"action\"", (SiteAction?)null,
            ("waitFor", SiteAction.WaitFor), ("countdown", SiteAction.Countdown), ("captcha", SiteAction.Captcha),
            ("click", SiteAction.Click), ("wait", SiteAction.Wait))
            ?? throw new InvalidConfigException($"{at} has no \"action\".");

        var needsSelector = action is SiteAction.WaitFor or SiteAction.Countdown or SiteAction.Click;
        var selector = raw.Selector?.Trim();
        if (needsSelector && string.IsNullOrEmpty(selector))
            throw new InvalidConfigException($"{at} ({Name(action)}) needs a \"selector\".");
        if (!needsSelector && raw.Selector is not null)
            throw new InvalidConfigException($"{at} ({Name(action)}) doesn't take a \"selector\".");

        if (action != SiteAction.WaitFor && raw.State is not null)
            throw new InvalidConfigException($"{at} ({Name(action)}) doesn't take a \"state\"; only waitFor does.");
        var state = Choose(raw.State, $"{at}'s \"state\"", WaitState.Visible,
            ("visible", WaitState.Visible), ("hidden", WaitState.Hidden), ("attached", WaitState.Attached));

        double seconds = 0;
        if (action == SiteAction.Wait)
        {
            seconds = raw.Seconds ?? throw new InvalidConfigException($"{at} (wait) needs \"seconds\".");
            if (seconds is <= 0 or > MaxSeconds)
                throw new InvalidConfigException($"{at} (wait): \"seconds\" has to be more than 0 and at most {MaxSeconds}.");
            if (raw.TimeoutSeconds is not null)
                throw new InvalidConfigException($"{at} (wait) takes \"seconds\", not \"timeoutSeconds\".");
        }
        else if (raw.Seconds is not null)
        {
            throw new InvalidConfigException($"{at} ({Name(action)}) doesn't take \"seconds\"; use \"timeoutSeconds\".");
        }

        var timeout = raw.TimeoutSeconds ?? DefaultTimeoutSeconds;
        if (timeout is < 1 or > MaxSeconds)
            throw new InvalidConfigException($"{at}: \"timeoutSeconds\" has to be between 1 and {MaxSeconds}.");

        return new SiteStep(action, needsSelector ? selector : null, state, timeout, seconds, raw.Optional ?? false);
    }

    private static string Name(SiteAction action) => JsonNamingPolicy.CamelCase.ConvertName(action.ToString());

    /// <summary>The value named by <paramref name="text"/> (exact, case-sensitive), <paramref name="fallback"/> when it's left out.</summary>
    private static T Choose<T>(string? text, string what, T fallback, params (string Name, T Value)[] choices)
    {
        if (text is null) return fallback;
        foreach (var (name, value) in choices)
            if (name == text) return value;
        throw new InvalidConfigException(
            $"{what}: \"{text}\" isn't one TurboDL knows ({string.Join(", ", choices.Select(c => c.Name))}).");
    }

    private sealed class InvalidConfigException(string message) : Exception(message);

    // What the JSON may hold. Everything is nullable so a missing value can be told apart from a default one.
    private sealed class ConfigJson
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string?>? Hosts { get; set; }
        public bool? BrowserFirst { get; set; }
        public List<StepJson?>? Steps { get; set; }
        public CaptureJson? Capture { get; set; }
        public string? Popups { get; set; }
        public string? OffSiteRedirects { get; set; }
        public string? WhenStepsEnd { get; set; }
    }

    private sealed class StepJson
    {
        public string? Action { get; set; }
        public string? Selector { get; set; }
        public string? State { get; set; }
        public int? TimeoutSeconds { get; set; }
        public double? Seconds { get; set; }
        public bool? Optional { get; set; }
    }

    private sealed class CaptureJson
    {
        public string? UrlPattern { get; set; }
        public List<string?>? FileHosts { get; set; }
        public bool? RequireAttachment { get; set; }
    }
}
