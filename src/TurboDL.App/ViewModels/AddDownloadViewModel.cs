using System.Diagnostics.CodeAnalysis;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TurboDL.App.Services;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;

namespace TurboDL.App.ViewModels;

public enum NoteKind { Neutral, Warning, Error }

public sealed partial class AddDownloadViewModel(DownloadManager manager, CategoryRules rules) : ObservableObject
{
    private CancellationTokenSource? _probeCts;
    private bool _nameEdited;
    private bool _folderEdited;
    private bool _updating;
    private IReadOnlyDictionary<string, string> _headers = new Dictionary<string, string>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial string FileName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial string Folder { get; set; } = "";

    [ObservableProperty] public partial double? Connections { get; set; } = 8;
    [ObservableProperty] public partial string Category { get; private set; } = CategoryRules.Other;
    [ObservableProperty] public partial string Note { get; private set; } = "";
    [ObservableProperty] public partial NoteKind NoteKind { get; private set; }
    [ObservableProperty] public partial bool IsChecking { get; private set; }

    /// <summary>Set when the user confirms; read by whoever opened the dialog.</summary>
    public DownloadInfo? Result { get; private set; }
    public bool StartImmediately { get; private set; }

    public event Action? CloseRequested;

    /// <summary>Fills in the URL (from the caller, else from the clipboard if it holds a link).</summary>
    public void Prefill(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        _headers = headers ?? new Dictionary<string, string>();
        Url = url ?? ClipboardLink() ?? "";
        if (Url.Length == 0) UpdateFolder();
    }

    partial void OnUrlChanged(string value)
    {
        _probeCts?.Cancel();
        if (!TryParse(value, out var uri))
        {
            IsChecking = false;
            SetNote(value.Trim().Length == 0 ? "" : "Enter a link that starts with http:// or https://", NoteKind.Error);
            return;
        }

        if (!_nameEdited) SetQuietly(() => FileName = FileNames.Sanitize(Uri.UnescapeDataString(uri.Segments[^1])));
        UpdateFolder();
        _ = ProbeAsync(uri.ToString(), (_probeCts = new CancellationTokenSource()).Token);
    }

    partial void OnFileNameChanged(string value)
    {
        if (!_updating) _nameEdited = true;
        UpdateFolder();
    }

    partial void OnFolderChanged(string value)
    {
        if (!_updating) _folderEdited = true;
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save", InitialDirectory = ExistingParent(Folder) };
        if (dialog.ShowDialog() == true) Folder = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void StartNow() => Accept(start: true);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddToQueue() => Accept(start: false);

    [RelayCommand]
    private void Cancel()
    {
        _probeCts?.Cancel();
        CloseRequested?.Invoke();
    }

    private bool CanAdd() => TryParse(Url, out _) && Folder.Trim().Length > 0 && Path.IsPathFullyQualified(Folder.Trim());

    private void Accept(bool start)
    {
        _probeCts?.Cancel();
        if (!TryParse(Url, out var uri)) return;
        Result = new DownloadInfo
        {
            Url = uri.ToString(),
            FileName = string.IsNullOrWhiteSpace(FileName) ? "" : FileNames.Sanitize(FileName),
            Directory = Folder.Trim(),
            Category = Category,
            Connections = (int)Math.Clamp(Connections ?? 8, 1, 32),
            Headers = new Dictionary<string, string>(_headers),
        };
        StartImmediately = start;
        CloseRequested?.Invoke();
    }

    private async Task ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            await Task.Delay(350, ct); // let typing settle
            IsChecking = true;
            SetNote("Checking the link…", NoteKind.Neutral);
            var probe = await UrlProbe.ProbeAsync(manager.Http, url, _headers, ct);
            if (ct.IsCancellationRequested) return;

            if (!_nameEdited) SetQuietly(() => FileName = probe.FileName);
            UpdateFolder();

            var size = probe.Length is long len ? Format.Bytes(len) : "Size unknown";
            var resumable = probe.SupportsRanges && probe.Length is not null;
            var duplicate = manager.Items.Any(i => string.Equals(i.Info.Url, url, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
                SetNote($"{size}. This link is already in your list.", NoteKind.Warning);
            else if (resumable)
                SetNote($"{size}, can pause and resume", NoteKind.Neutral);
            else
                SetNote($"{size}. The server doesn't allow resuming, so this downloads on one connection.", NoteKind.Warning);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetNote($"Couldn't check the link: {ex.GetBaseException().Message} You can still add it and TurboDL will retry.",
                NoteKind.Error);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsChecking = false;
        }
    }

    private void UpdateFolder()
    {
        Category = rules.CategoryFor(FileName);
        if (!_folderEdited) SetQuietly(() => Folder = rules.FolderFor(Category));
    }

    private void SetQuietly(Action set)
    {
        _updating = true;
        try { set(); }
        finally { _updating = false; }
    }

    private void SetNote(string text, NoteKind kind)
    {
        Note = text;
        NoteKind = kind;
    }

    private static bool TryParse(string? text, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? ClipboardLink()
    {
        try
        {
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : null;
            return text is not null && !text.Contains('\n') && TryParse(text, out _) ? text : null;
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
