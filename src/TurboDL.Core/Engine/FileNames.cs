namespace TurboDL.Core.Engine;

public static class FileNames
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        var chars = name.Trim().Trim('"').Select(c => Array.IndexOf(Invalid, c) >= 0 ? '_' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) return "download";
        if (clean.Length > 200)
        {
            var ext = Path.GetExtension(clean);
            clean = clean[..(200 - ext.Length)] + ext;
        }
        return clean;
    }

    /// <summary>Returns <paramref name="name"/> or "name (n).ext" so it clashes with neither a finished file nor a partial one.</summary>
    public static string MakeUnique(string directory, string name)
    {
        bool Taken(string n)
        {
            var p = Path.Combine(directory, n);
            return File.Exists(p) || File.Exists(p + ".turbodl");
        }

        if (!Taken(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }
    }
}
