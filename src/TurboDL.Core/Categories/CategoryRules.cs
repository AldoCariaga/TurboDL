namespace TurboDL.Core.Categories;

/// <summary>
/// Maps a file name to a category (by extension) and a category to a folder.
/// Starts with built-in defaults; Settings replaces the rules with <see cref="Update"/>.
/// </summary>
public sealed class CategoryRules
{
    public const string Other = "Other";

    private Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories in display order. <see cref="Other"/> is always last.</summary>
    public IReadOnlyList<string> Categories { get; private set; } = [Other];

    /// <summary>Each category gets a sub-folder here, e.g. <c>Downloads\TurboDL\Video</c>.</summary>
    public string RootFolder { get; private set; }

    /// <summary>Raised after <see cref="Update"/> so lists of categories can rebuild.</summary>
    public event Action? Changed;

    public CategoryRules(string rootFolder, IEnumerable<(string Category, string[] Extensions)>? rules = null)
    {
        RootFolder = rootFolder;
        Apply(rules ?? Defaults);
    }

    public static IReadOnlyList<(string Category, string[] Extensions)> Defaults { get; } =
    [
        ("Video", ["mp4", "mkv", "webm", "avi", "mov", "wmv", "flv", "m4v", "mpg", "mpeg", "ts", "3gp"]),
        ("Music", ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "alac"]),
        ("Documents", ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "txt", "rtf", "epub", "csv"]),
        ("Compressed", ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "iso", "img", "cab"]),
        ("Programs", ["exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "bat", "cmd", "ps1", "apk"]),
    ];

    public void Update(string rootFolder, IEnumerable<(string Category, string[] Extensions)> rules)
    {
        RootFolder = rootFolder;
        Apply(rules);
        Changed?.Invoke();
    }

    public string CategoryFor(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && _byExtension.TryGetValue(ext, out var category) ? category : Other;
    }

    public string FolderFor(string category) => Path.Combine(RootFolder, category);

    private void Apply(IEnumerable<(string Category, string[] Extensions)> rules)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var (category, extensions) in rules)
        {
            var name = category.Trim();
            if (name.Length == 0 || name.Equals(Other, StringComparison.OrdinalIgnoreCase)) continue;
            if (list.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(name);
            foreach (var ext in extensions)
                map.TryAdd(ext.TrimStart('.'), name); // first rule wins when two claim an extension
        }
        list.Add(Other);
        _byExtension = map;
        Categories = list;
    }
}
