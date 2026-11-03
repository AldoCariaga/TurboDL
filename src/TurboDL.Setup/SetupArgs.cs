namespace TurboDL.Setup;

/// <summary>
/// Setup's command line.
/// <list type="bullet">
/// <item><c>(nothing)</c>: the install window. <c>/uninstall</c>: the uninstall window (Installed apps starts that).</item>
/// <item><c>/S</c>: silent, no window; with <c>/uninstall</c> it uninstalls. <c>/dir=&lt;folder&gt;</c> picks the install folder,
/// <c>/startmenu=0|1</c>, <c>/desktop=0|1</c>, <c>/startup=0|1</c> override the options, <c>/removedata</c> also deletes the
/// download list and settings on uninstall, <c>/log=&lt;file&gt;</c> writes what happened.</item>
/// <item>Internal: <c>/worker=install|uninstall</c> (the elevated half), <c>/testroot=&lt;folder&gt;</c> (everything goes to
/// scratch folders and a scratch registry key), <c>/relocated</c> (uninstall running from its temp copy).</item>
/// </list>
/// </summary>
public sealed record SetupArgs
{
    public bool Uninstall { get; init; }
    public bool Silent { get; init; }
    public string? Worker { get; init; }
    public string? InstallDir { get; init; }
    public string? TestRoot { get; init; }
    public string? Log { get; init; }
    public bool RemoveData { get; init; }
    public bool Relocated { get; init; }
    public bool? StartMenu { get; init; }
    public bool? Desktop { get; init; }
    public bool? Startup { get; init; }

    public static SetupArgs Parse(IEnumerable<string> args)
    {
        var result = new SetupArgs();
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0) continue;
            if (arg[0] is not ('/' or '-')) throw new ArgumentException($"Unknown argument \"{raw}\".");
            var eq = arg.IndexOf('=');
            var name = (eq < 0 ? arg[1..] : arg[1..eq]).ToLowerInvariant();
            var value = eq < 0 ? null : arg[(eq + 1)..].Trim('"');

            result = name switch
            {
                "uninstall" => result with { Uninstall = true },
                "s" or "silent" => result with { Silent = true },
                "worker" => result with { Worker = value is "install" or "uninstall" ? value : throw new ArgumentException("/worker needs install or uninstall.") },
                "dir" => result with { InstallDir = Folder(name, value) },
                "testroot" => result with { TestRoot = Folder(name, value) },
                "log" => result with { Log = Folder(name, value, "file") },
                "removedata" => result with { RemoveData = true },
                "relocated" => result with { Relocated = true },
                "startmenu" => result with { StartMenu = Flag(raw, value) },
                "desktop" => result with { Desktop = Flag(raw, value) },
                "startup" => result with { Startup = Flag(raw, value) },
                _ => throw new ArgumentException($"Unknown argument \"{raw}\"."),
            };
        }
        return result;
    }

    /// <summary>A path value; an empty one is a mistake worth naming (/testroot= with nothing after it).</summary>
    private static string Folder(string name, string? value, string what = "folder") =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"/{name} needs a {what}, e.g. /{name}=C:\\Scratch.") : value;

    /// <summary>
    /// Whether these raw arguments ask for no window: /S, or the worker. Used when they can't even be parsed, so a
    /// silent or worker run never stops on a message box.
    /// </summary>
    public static bool AsksForNoWindow(IEnumerable<string> args) =>
        args.Any(a => a.Trim() is var t && (t.Equals("/S", StringComparison.OrdinalIgnoreCase) || t.Equals("-S", StringComparison.OrdinalIgnoreCase)
            || t.Equals("/silent", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("/worker", StringComparison.OrdinalIgnoreCase) || t.StartsWith("-worker", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The /log=&lt;file&gt; value from raw arguments, for reporting a command line that can't be parsed.</summary>
    public static string? RawLog(IEnumerable<string> args) =>
        args.Select(a => a.Trim()).FirstOrDefault(a => a.StartsWith("/log=", StringComparison.OrdinalIgnoreCase) && a.Length > 5)?[5..].Trim('"');

    private static bool Flag(string raw, string? value) => value switch
    {
        null or "1" or "true" or "yes" => true,
        "0" or "false" or "no" => false,
        _ => throw new ArgumentException($"\"{raw}\": use 0 or 1."),
    };

    /// <summary>The arguments that carry the same targets (folder, test root) to another copy of Setup.</summary>
    public IEnumerable<string> TargetArgs()
    {
        if (InstallDir is not null) yield return $"/dir={InstallDir}";
        if (TestRoot is not null) yield return $"/testroot={TestRoot}";
    }
}
