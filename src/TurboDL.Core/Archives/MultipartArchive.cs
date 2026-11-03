using System.Globalization;
using System.Text.RegularExpressions;

namespace TurboDL.Core.Archives;

/// <summary>How the volumes of an archive are named.</summary>
public enum VolumeScheme
{
    /// <summary>name.part1.rar, name.part01.rar, name.part001.rar …</summary>
    RarParts,
    /// <summary>name.7z.001, name.7z.002 … and name.zip.001, name.zip.002 … (a file cut into pieces; 7-Zip joins them).</summary>
    Split,
    /// <summary>One file: name.rar, name.7z or name.zip.</summary>
    Single,
}

/// <summary>The volumes of one archive found in a list of file names.</summary>
/// <param name="BaseName">"data" for data.part01.rar; also the extraction subfolder name.</param>
/// <param name="Scheme">How the volumes are named.</param>
/// <param name="Files">The file names present, ordered by volume number.</param>
/// <param name="Count">Highest volume number seen (1 for Single).</param>
/// <param name="Missing">Volume numbers between 1 and Count that aren't present.</param>
public sealed record VolumeSet(string BaseName, VolumeScheme Scheme, IReadOnlyList<string> Files, int Count, IReadOnlyList<int> Missing)
{
    /// <summary>True when no volume between 1 and <see cref="Count"/> is missing.</summary>
    public bool IsComplete => Missing.Count == 0;

    /// <summary>The file 7-Zip is pointed at (volume 1), or null when volume 1 is missing.</summary>
    public string? FirstVolume => Files.FirstOrDefault(f => MultipartArchive.TryParse(f, out _, out _, out var n) && n == 1);
}

/// <summary>Recognises archive volume names and groups them into sets. Old-style .r00 volumes aren't supported.</summary>
public static partial class MultipartArchive
{
    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d{1,4})\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPartsName();

    [GeneratedRegex(@"^(?<base>.+)\.(?<ext>7z|zip)\.(?<n>\d{3,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitName();

    [GeneratedRegex(@"^(?<base>.+)\.(?<ext>rar|7z|zip)$", RegexOptions.IgnoreCase)]
    private static partial Regex SingleName();

    /// <summary>Recognises a volume name; false for anything that isn't part of an archive.</summary>
    public static bool TryParse(string fileName, out string baseName, out VolumeScheme scheme, out int number) =>
        TryParse(fileName, out baseName, out scheme, out _, out number);

    /// <summary>As the public overload, plus <paramref name="ext"/>: "rar", "7z" or "zip", lower case.</summary>
    internal static bool TryParse(string fileName, out string baseName, out VolumeScheme scheme, out string ext, out int number)
    {
        var name = Path.GetFileName(fileName);

        var m = RarPartsName().Match(name);
        if (m.Success && (number = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)) >= 1)
        {
            baseName = m.Groups["base"].Value;
            scheme = VolumeScheme.RarParts;
            ext = "rar";
            return true;
        }

        m = SplitName().Match(name);
        if (m.Success && (number = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)) >= 1)
        {
            baseName = m.Groups["base"].Value;
            scheme = VolumeScheme.Split;
            ext = m.Groups["ext"].Value.ToLowerInvariant();
            return true;
        }

        m = SingleName().Match(name);
        if (m.Success)
        {
            baseName = m.Groups["base"].Value;
            scheme = VolumeScheme.Single;
            ext = m.Groups["ext"].Value.ToLowerInvariant();
            number = 1;
            return true;
        }

        baseName = "";
        scheme = default;
        ext = "";
        number = 0;
        return false;
    }

    /// <summary>
    /// Groups names into archive sets; names that aren't archives are ignored. Ordered by BaseName.
    /// A name seen twice (in any case) counts once. Missing volumes are only found up to the highest number in the
    /// list: if the last parts aren't in the list at all, nothing here can tell, and the set looks complete.
    /// </summary>
    public static IReadOnlyList<VolumeSet> Group(IEnumerable<string> fileNames)
    {
        var sets = new Dictionary<(string Base, VolumeScheme Scheme, string Ext), SortedDictionary<int, string>>();
        var bases = new Dictionary<(string, VolumeScheme, string), string>();
        foreach (var raw in fileNames)
        {
            var name = Path.GetFileName(raw);
            if (!TryParse(name, out var baseName, out var scheme, out var ext, out var number)) continue;
            var key = (baseName.ToLowerInvariant(), scheme, ext);
            if (!sets.TryGetValue(key, out var volumes))
            {
                sets[key] = volumes = [];
                bases[key] = baseName;
            }
            volumes.TryAdd(number, name);
        }

        return [.. sets
            .Select(kv =>
            {
                var count = kv.Value.Keys.Max();
                var missing = Enumerable.Range(1, count).Where(n => !kv.Value.ContainsKey(n)).ToList();
                return new VolumeSet(bases[kv.Key], kv.Key.Scheme, [.. kv.Value.Values], count, missing);
            })
            .OrderBy(s => s.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Scheme)
            .ThenBy(s => s.Files[0], StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"data.part01–50.rar", "data.7z.001–003", "testdata.zip.001–003", "data.rar" for summaries.</summary>
    public static string Describe(VolumeSet set)
    {
        var first = set.Files[0];
        if (set.Scheme == VolumeScheme.Single || set.Files.Count == 1) return first;

        var last = set.Files[^1];
        if (set.Scheme == VolumeScheme.RarParts)
        {
            var lastNumber = RarPartsName().Match(last).Groups["n"].Value;
            return first[..^".rar".Length] + "–" + lastNumber + first[^".rar".Length..];
        }
        return first + "–" + SplitName().Match(last).Groups["n"].Value;
    }
}
