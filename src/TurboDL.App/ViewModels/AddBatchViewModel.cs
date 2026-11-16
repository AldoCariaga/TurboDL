using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TurboDL.App.Services;
using TurboDL.Core.Archives;
using TurboDL.Core.Batches;
using TurboDL.Core.Catching;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;

namespace TurboDL.App.ViewModels;

/// <summary>
/// The Add batch dialog: many links pasted at once (any text around them is fine), saved together into one folder and
/// extracted when every part is in. Blocks what would break an archive set: a folder that already holds some of the
/// files, or two links that would save under the same name. Also blocks a folder name Windows can't use.
/// </summary>
public sealed partial class AddBatchViewModel(DownloadManager manager, CategoryRules rules, SettingsService settings)
    : ObservableObject
{
    private const string Compressed = "Compressed";

    private IReadOnlyList<Uri> _links = [];
    private IReadOnlyList<string> _names = [];

    /// <summary>Adding to a batch: the names its parts already have (they count towards its archive sets).</summary>
    private IReadOnlyList<string> _existing = [];
    private bool _folderEdited;
    private bool _updating;
    private bool _blocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial string Folder { get; set; } = "";

    [ObservableProperty] public partial string Cookie { get; set; } = "";
    [ObservableProperty] public partial string Referer { get; set; } = "";
    [ObservableProperty] public partial string UserAgent { get; set; } = "";
    [ObservableProperty] public partial bool AutoExtract { get; set; } = true;
    [ObservableProperty] public partial bool DeleteParts { get; set; }

    /// <summary>Where to extract; empty = &lt;Folder&gt;\Extracted.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial string ExtractTo { get; set; } = "";

    /// <summary>What an empty Extract to means: "&lt;Save to&gt;\Extracted".</summary>
    [ObservableProperty] public partial string ExtractToPlaceholder { get; private set; } = "";
    [ObservableProperty] public partial double? Connections { get; set; } = 8;

    /// <summary>What the pasted text holds: how many links, which archive sets, and what's already in the list.</summary>
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    [ObservableProperty] public partial NoteKind SummaryKind { get; private set; }

    /// <summary>True when the links go into an existing batch: its folder and extract settings stay as they are.</summary>
    [ObservableProperty] public partial bool IsAddingToBatch { get; private set; }

    [ObservableProperty] public partial string Title { get; private set; } = "Add batch";

    /// <summary>Set when the user confirms; read by whoever opened the dialog.</summary>
    public BatchRequest? Result { get; private set; }

    public event Action? CloseRequested;

    /// <summary>Fills in the links from the caller, else from the clipboard when it holds two or more links (one when adding to a batch).</summary>
    public void Prefill(string? text)
    {
        Connections = settings.Current.DefaultConnections;
        // Adding to a batch, one copied link is enough: it's often the one part that was missing.
        var value = text ?? ClipboardLinks(IsAddingToBatch ? 1 : 2) ?? "";
        if (value == Text) Update();
        else Text = value;
    }

    /// <summary>
    /// Adding links to <paramref name="batch"/>: its folder is fixed, and the names of <paramref name="parts"/> count
    /// towards its archive sets (so a missing part shows as found) and can't be added twice.
    /// </summary>
    public void PrefillForBatch(BatchViewModel batch, IReadOnlyList<DownloadItemViewModel> parts)
    {
        IsAddingToBatch = true;
        Title = $"Add links to {batch.Name}";
        _existing = [.. parts.Select(p => p.Info.RequestedFileName is { Length: > 0 } r ? r : p.Info.FileName).Where(n => n.Length > 0)];
        _folderEdited = true;
        SetQuietly(() => Folder = batch.Info.Directory);
        AutoExtract = batch.Info.AutoExtract;
        DeleteParts = batch.Info.DeleteParts;
        Prefill(null);
        if (parts.FirstOrDefault() is { } first) Connections = first.Info.Connections;
    }

    partial void OnTextChanged(string value)
    {
        _links = UrlExtractor.ExtractAll(value);
        _names = [.. _links.Select(NameFromUrl).Where(n => n.Length > 0)];
        Update();
    }

    partial void OnFolderChanged(string value)
    {
        if (_updating) return;
        _folderEdited = true;
        Update();
    }

    partial void OnAutoExtractChanged(bool value)
    {
        if (!value) DeleteParts = false;
        Update();
    }

    partial void OnExtractToChanged(string value) => Update();

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save", InitialDirectory = ExistingParent(Folder) };
        if (dialog.ShowDialog() == true) Folder = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseExtractTo()
    {
        var start = ExistingParent(ExtractTo.Trim().Length > 0 ? ExtractTo.Trim() : Folder.Trim());
        var dialog = new OpenFolderDialog { Title = "Choose where to extract", InitialDirectory = start };
        if (dialog.ShowDialog() == true) ExtractTo = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void StartNow() => Accept(start: true);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddToQueue() => Accept(start: false);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private bool CanAdd() =>
        _links.Count > 0 && Folder.Trim().Length > 0 && Path.IsPathFullyQualified(Folder.Trim()) && !_blocked
        && (!AutoExtract || ExtractTo.Trim().Length == 0 || Path.IsPathFullyQualified(ExtractTo.Trim()));

    private void Accept(bool start)
    {
        if (!CanAdd()) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cookie = Cookie.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = cookie["Cookie:".Length..].Trim();
        if (cookie.Length > 0) headers["Cookie"] = cookie;
        if (Referer.Trim() is { Length: > 0 } referer) headers["Referer"] = referer;
        if (UserAgent.Trim() is { Length: > 0 } userAgent) headers["User-Agent"] = userAgent;

        var extractTo = AutoExtract && ExtractTo.Trim() is { Length: > 0 } chosen ? chosen : null;
        Result = new BatchRequest(_links, Folder.Trim(), headers, AutoExtract, DeleteParts, start,
            (int)Math.Clamp(Connections ?? 8, 1, 32), extractTo);
        CloseRequested?.Invoke();
    }

    /// <summary>Picks the folder (until the user edits it), rewrites the summary and checks for clashes.</summary>
    private void Update()
    {
        var sets = MultipartArchive.Group(_existing.Concat(_names));
        if (!_folderEdited)
        {
            // One folder per batch, named like the batch: its parts, and Extracted\ with every set in it.
            var folder = Path.Combine(rules.FolderFor(Compressed), FileNames.Sanitize(BatchPlanner.SuggestName(_names, DateTime.Now)));
            SetQuietly(() => Folder = folder);
        }

        ExtractToPlaceholder = Folder.Trim() is { Length: > 0 } saveTo
            ? $"{Path.Combine(saveTo, "Extracted")} (default)"
            : "A folder named Extracted next to the parts (default)";

        var (summary, kind) = Describe(sets);
        _blocked = kind == NoteKind.Error;
        Summary = summary;
        SummaryKind = kind;
        StartNowCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
    }

    private (string, NoteKind) Describe(IReadOnlyList<VolumeSet> sets)
    {
        if (_links.Count == 0)
            return Text.Trim().Length == 0
                ? ("", NoteKind.Neutral)
                : ("No links found. Paste one link per line, or any text that contains links.", NoteKind.Error);

        if (_names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return ($"Two links would save as {twice.Key}. Remove one of them.", NoteKind.Error);

        if (_names.FirstOrDefault(n => _existing.Contains(n, StringComparer.OrdinalIgnoreCase)) is { } inBatch)
            return ($"{inBatch} is already in this batch. Remove that link, or right-click the part and choose Change link.", NoteKind.Error);

        if (HasBadCharacters(Folder.Trim()))
            return ("The folder name has a character Windows doesn't allow (a line break, or one of < > : \" | ? *). " +
                    "Choose another folder.", NoteKind.Error);

        if (AutoExtract && HasBadCharacters(ExtractTo.Trim()))
            return ("The Extract to folder has a character Windows doesn't allow (a line break, or one of < > : \" | ? *). " +
                    "Choose another folder.", NoteKind.Error);

        if (AutoExtract && ExtractTo.Trim() is { Length: > 0 } extractTo && !Path.IsPathFullyQualified(extractTo))
            return ("Extract to needs a full path, such as D:\\Extracted.", NoteKind.Error);

        if (FolderHoldsAny())
            return ("Some of these files are already in this folder. TurboDL would rename the new copies, and the archive " +
                    "couldn't be extracted. Choose an empty folder.", NoteKind.Error);

        var parts = new List<string> { _links.Count == 1 ? "1 link" : $"{_links.Count} links" };
        foreach (var set in sets)
            parts.Add(MultipartArchive.Describe(set) + set.Missing.Count switch
            {
                0 => "",
                1 => $" (part {set.Missing[0]} missing)",
                _ => $" (parts {string.Join(", ", set.Missing)} missing)",
            });

        var duplicates = _links.Count(l =>
            manager.Items.Any(i => string.Equals(i.Info.Url, l.ToString(), StringComparison.OrdinalIgnoreCase)));
        if (duplicates > 0) parts.Add($"{duplicates} already in your list");

        var warn = duplicates > 0 || sets.Any(s => !s.IsComplete);
        return (string.Join(" · ", parts), warn ? NoteKind.Warning : NoteKind.Neutral);
    }

    /// <summary>True when the chosen folder already holds a file (or an unfinished download) under one of the names.</summary>
    private bool FolderHoldsAny()
    {
        var folder = Folder.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return false;
        return _names.Any(n =>
        {
            var path = Path.Combine(folder, n);
            return File.Exists(path) || File.Exists(path + ".turbodl");
        });
    }

    /// <summary>
    /// True when the folder can't be created as typed: a control character such as a line break, a character not
    /// allowed in a folder name, or a path Windows refuses. An empty or relative folder isn't reported here.
    /// </summary>
    private static bool HasBadCharacters(string folder)
    {
        if (folder.Length == 0) return false;
        if (folder.Any(char.IsControl) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return true;

        var root = Path.GetPathRoot(folder) ?? "";
        var names = folder[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (names.Any(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return true;

        try
        {
            Path.GetFullPath(folder);
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private void SetQuietly(Action set)
    {
        _updating = true;
        try { set(); }
        finally { _updating = false; }
    }

    /// <summary>The file name a link gives, or "" when its last segment has no extension (the server names it).</summary>
    private static string NameFromUrl(Uri uri)
    {
        var last = Uri.UnescapeDataString(uri.Segments[^1]).TrimEnd('/');
        return last.Length == 0 || !Path.HasExtension(last) ? "" : FileNames.Sanitize(last);
    }

    private static string? ClipboardLinks(int atLeast)
    {
        try
        {
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
            return UrlExtractor.ExtractAll(text).Count >= atLeast ? text : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // another app holds the clipboard
        }
    }

    private static string? ExistingParent(string? folder)
    {
        for (var dir = folder; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir)) return dir;
        return null;
    }
}
