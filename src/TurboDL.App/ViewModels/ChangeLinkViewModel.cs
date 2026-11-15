using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TurboDL.Core.Archives;
using TurboDL.Core.Engine;

namespace TurboDL.App.ViewModels;

/// <summary>What the Change link dialog asks for: the new link and the name to save under ("" = the server's).</summary>
public sealed record ChangeLinkRequest(Uri Url, string FileName);

/// <summary>
/// The Change link dialog: a stopped download pointed at another link (an expired one, a mirror), keeping its name
/// unless the user changes it. Blocks a link that isn't http(s) and a name Windows can't use; warns when a batch part's
/// new name no longer fits its archive set, or the link is already in the list.
/// </summary>
public sealed partial class ChangeLinkViewModel : ObservableObject
{
    private readonly DownloadItemViewModel _item;
    private readonly IReadOnlyList<DownloadItemViewModel> _all;
    private readonly string _oldName;
    private bool _blocked = true;

    public ChangeLinkViewModel(DownloadItemViewModel item, IReadOnlyList<DownloadItemViewModel> all)
    {
        _item = item;
        _all = all;
        _oldName = item.Info.RequestedFileName is { Length: > 0 } requested ? requested : item.Info.FileName;
        CurrentLink = item.OriginalLink;
        Link = item.OriginalLink;
        FileName = _oldName;
    }

    /// <summary>The link the download has now, shown above the box.</summary>
    public string CurrentLink { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Link { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string FileName { get; set; } = "";

    [ObservableProperty] public partial string Note { get; private set; } = "";
    [ObservableProperty] public partial NoteKind NoteKind { get; private set; }

    /// <summary>Set when the user saves; read by whoever opened the dialog.</summary>
    public ChangeLinkRequest? Result { get; private set; }

    public event Action? CloseRequested;

    partial void OnLinkChanged(string value) => Update();
    partial void OnFileNameChanged(string value) => Update();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave() || ParseLink() is not { } url) return;
        Result = new ChangeLinkRequest(url, FileName.Trim());
        CloseRequested?.Invoke();
    }

    private bool CanSave() => !_blocked;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private Uri? ParseLink() =>
        Uri.TryCreate(Link.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    private void Update()
    {
        var (note, kind) = Describe();
        _blocked = kind == NoteKind.Error;
        Note = note;
        NoteKind = kind;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private (string, NoteKind) Describe()
    {
        if (Link.Trim().Length == 0) return ("Paste the new link.", NoteKind.Error);
        if (ParseLink() is not { } url) return ("That isn't a web link. It should start with http:// or https://.", NoteKind.Error);

        var name = FileName.Trim();
        if (name.Length > 0 && FileNames.Sanitize(name) != name)
            return ("The file name has a character Windows doesn't allow (one of \\ / : * ? \" < > |).", NoteKind.Error);

        if (_item.Info.BatchId is not null && !string.Equals(name, _oldName, StringComparison.OrdinalIgnoreCase)
            && MultipartArchive.TryParse(_oldName, out _, out _, out _))
            return ($"This part of the batch was {_oldName}. Under another name the archive can't be extracted.", NoteKind.Warning);

        if (_all.Any(i => i != _item && string.Equals(i.Info.Url, url.ToString(), StringComparison.OrdinalIgnoreCase)))
            return ("This link is already in your list.", NoteKind.Warning);

        if (name.Length > 0 && !string.Equals(name, _item.Info.FileName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(_item.Info.Directory) && File.Exists(Path.Combine(_item.Info.Directory, name)))
            return ($"{name} is already in the folder, so the new file gets a name like {Path.GetFileNameWithoutExtension(name)} (2).", NoteKind.Warning);

        return (name.Length == 0
            ? "Downloads again from the new link, named by the server. What came from the old link is thrown away."
            : "Downloads again from the new link. What came from the old link is thrown away.", NoteKind.Neutral);
    }
}
