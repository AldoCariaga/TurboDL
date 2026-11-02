namespace TurboDL.Core.Categories;

/// <summary>
/// Maps a file name to a category (by extension) and a category to a folder.
/// Built-in defaults for now; the Settings page will make the lists editable.
/// </summary>
public sealed class CategoryRules
{
    public const string Other = "Other";

    private readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories in display order. <see cref="Other"/> is always last.</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>Each category gets a sub-folder here, e.g. <c>Downloads\TurboDL\Video</c>.</summary>
    public string RootFolder { get; set; }

    public CategoryRules(string rootFolder, IEnumerable<(string Category, string[] Extensions)>? rules = null)
    {
        RootFolder = rootFolder;
        var list = new List<string>();
        foreach (var (category, extensions) in rules ?? Defaults)
        {
            if (category == Other) continue;
            list.Add(category);
            foreach (var ext in extensions)
                _byExtension.TryAdd(ext.TrimStart('.'), category);
        }
        list.Add(Other);
        Categories = list;
    }

    public static IReadOnlyList<(string Category, string[] Extensions)> Defaults { get; } =
    [
        ("Video", ["mp4", "mkv", "webm", "avi", "mov", "wmv", "flv", "m4v", "mpg", "mpeg", "ts", "3gp"]),
        ("Music", ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "alac"]),
        ("Documents", ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "txt", "rtf", "epub", "csv"]),
        ("Compressed", ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "iso", "img", "cab"]),
        ("Programs", ["exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "bat", "cmd", "ps1", "apk"]),
    ];

    public string CategoryFor(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && _byExtension.TryGetValue(ext, out var category) ? category : Other;
    }

    public string FolderFor(string category) => Path.Combine(RootFolder, category);
}
