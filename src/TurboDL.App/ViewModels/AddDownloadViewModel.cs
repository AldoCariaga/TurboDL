using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TurboDL.App.Services;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;
using TurboDL.Core.Video;

namespace TurboDL.App.ViewModels;

public enum NoteKind { Neutral, Warning, Error }

/// <summary>One entry of the video quality picker. <see cref="Quality"/> is null for "Choose a format…".</summary>
public sealed record QualityChoice(string Label, VideoQuality? Quality)
{
    public bool IsCustom => Quality is null;
    public override string ToString() => Label;
}

/// <summary>One yt-dlp format in the "Choose a format…" list.</summary>
public sealed record FormatChoice(VideoFormat Format, string Label)
{
    public override string ToString() => Label;

    public static FormatChoice From(VideoFormat f)
    {
        var parts = new List<string> { f.Id };
        if (f.IsAudioOnly) parts.Add("audio only");
        else if (f.Height is int h) parts.Add(f.Fps is double fps && fps > 0 ? $"{h}p{Math.Round(fps):0}" : $"{h}p");
        else if (!string.IsNullOrEmpty(f.Resolution)) parts.Add(f.Resolution);
        if (!string.IsNullOrEmpty(f.Ext)) parts.Add(f.Ext);
        var codecs = new[] { f.VCodec, f.ACodec }.Where(c => !string.IsNullOrEmpty(c) && c != "none").Select(c => c!.Split('.')[0]);
        parts.Add(string.Join("+", codecs) is { Length: > 0 } c ? c : "codec unknown");
        if (f.IsVideoOnly) parts.Add("video only, best audio added");
        if (f.Size is long size) parts.Add((f.FileSize is null ? "~" : "") + Services.Format.Bytes(size));
        return new FormatChoice(f, string.Join(" · ", parts));
    }
}

public sealed partial class AddDownloadViewModel(
    DownloadManager manager,
    CategoryRules rules,
    SettingsService settings,
    VideoToolsService videoTools)
    : ObservableObject
{
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _toolsCts;
    private bool _nameEdited;
    private bool _folderEdited;
    private bool _updating;
    private IReadOnlyDictionary<string, string> _headers = new Dictionary<string, string>();

    // Video: what addVideo asked for, the URL yt-dlp accepted, and whether setting up the tools was cancelled or failed.
    private bool _videoFirst;
    private string? _pageUrl;
    private string? _videoUrl;
    private VideoInfo? _video;
    private bool _skipVideoSetup;

    /// <summary>
    /// The link answered with a web page that has no video: a file host's waiting page. The download is then added as
    /// one that must give a file, so when it starts TurboDL sees the page and opens it in a browser to click through.
    /// </summary>
    private bool _hostPage;

    private const string HostPageNote =
        "This link opens a web page, not the file. TurboDL will open it in a browser and click through to the file; " +
        "solve any CAPTCHA or sign-in there yourself.";

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

    // ---- Video mode ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial bool IsVideoMode { get; private set; }

    [ObservableProperty] public partial string VideoTitle { get; private set; } = "";
    [ObservableProperty] public partial string VideoMeta { get; private set; } = "";
    [ObservableProperty] public partial string? Thumbnail { get; private set; }

    public IReadOnlyList<QualityChoice> Qualities { get; } =
    [
        new("Best available", VideoQuality.Best),
        new("Up to 1080p", VideoQuality.Max1080),
        new("Up to 720p", VideoQuality.Max720),
        new("Audio only", VideoQuality.AudioOnly),
        new("Choose a format…", null),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    [NotifyPropertyChangedFor(nameof(ShowFormats), nameof(ShowContainer))]
    public partial QualityChoice? SelectedQuality { get; set; }

    public ObservableCollection<FormatChoice> Formats { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    [NotifyPropertyChangedFor(nameof(ShowContainer))]
    public partial FormatChoice? SelectedFormat { get; set; }

    public IReadOnlyList<string> Containers { get; } = ["mp4", "mkv"];
    [ObservableProperty] public partial string SelectedContainer { get; set; } = "mp4";

    public bool ShowFormats => IsVideoMode && SelectedQuality?.IsCustom == true;

    /// <summary>The container only matters when the result is video.</summary>
    public bool ShowContainer => IsVideoMode && !IsAudioResult;

    private bool IsAudioResult => SelectedQuality?.IsCustom == true
        ? SelectedFormat?.Format.IsAudioOnly == true
        : SelectedQuality?.Quality == VideoQuality.AudioOnly;

    // ---- First-use setup of yt-dlp, ffmpeg and Deno (starts on its own; Cancel falls back to a plain file) ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNowCommand), nameof(AddToQueueCommand))]
    public partial bool IsSettingUpTools { get; private set; }

    [ObservableProperty] public partial string ToolsText { get; private set; } = "";
    [ObservableProperty] public partial double ToolsFraction { get; private set; }
    [ObservableProperty] public partial bool ToolsIndeterminate { get; private set; } = true;

    /// <summary>Set when the user confirms; read by whoever opened the dialog.</summary>
    public DownloadInfo? Result { get; private set; }
    public bool StartImmediately { get; private set; }

    public event Action? CloseRequested;

    /// <summary>
    /// Fills in the URL (from the caller, else from the clipboard if it holds a link). A file name from the caller
    /// (the browser's pick) is kept as if typed, so neither the URL nor the server's answer replaces it.
    /// </summary>
    public void Prefill(string? url, IReadOnlyDictionary<string, string>? headers, string? fileName = null)
    {
        _headers = headers ?? new Dictionary<string, string>();
        Connections = settings.Current.DefaultConnections;
        if (!string.IsNullOrWhiteSpace(fileName))
            FileName = FileNames.Sanitize(Path.GetFileName(fileName.Trim())); // marks it edited and picks the category folder
        Url = url ?? ClipboardLink() ?? "";
        if (Url.Length == 0) UpdateFolder();
    }

    /// <summary>The browser's "Download video with TurboDL": straight to yt-dlp, trying the page when the URL itself fails.</summary>
    public void PrefillVideo(string url, string? pageUrl, IReadOnlyDictionary<string, string>? headers)
    {
        _headers = headers ?? new Dictionary<string, string>();
        Connections = settings.Current.DefaultConnections;
        _pageUrl = TryParse(pageUrl, out var page) ? page.ToString() : null;
        _videoFirst = true;
        Url = url;
    }

    /// <summary>The window closed (by any means): stop probing and stop a tools download in progress.</summary>
    public void Abandon()
    {
        _probeCts?.Cancel();
        _toolsCts?.Cancel();
    }

    partial void OnUrlChanged(string value)
    {
        _probeCts?.Cancel();
        ExitVideoMode();
        if (!TryParse(value, out var uri))
        {
            IsChecking = false;
            SetNote(value.Trim().Length == 0 ? "" : "Enter a link that starts with http:// or https://", NoteKind.Error);
            return;
        }

        if (!_nameEdited) SetQuietly(() => FileName = FileNames.Sanitize(Uri.UnescapeDataString(uri.Segments[^1])));
        UpdateFolder();
        var ct = (_probeCts = new CancellationTokenSource()).Token;
        if (_videoFirst)
        {
            _videoFirst = false;
            _ = VideoFirstAsync(uri.ToString(), ct);
        }
        else
        {
            _ = ProbeAsync(uri.ToString(), ct);
        }
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

    partial void OnSelectedQualityChanged(QualityChoice? value)
    {
        if (value?.IsCustom == true && SelectedFormat is null) SelectedFormat = Formats.FirstOrDefault();
        UpdateFolder();
    }

    partial void OnSelectedFormatChanged(FormatChoice? value) => UpdateFolder();

    partial void OnIsVideoModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFormats));
        OnPropertyChanged(nameof(ShowContainer));
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
        Abandon();
        CloseRequested?.Invoke();
    }

    /// <summary>Stops the tools download; the link is then treated as a plain file.</summary>
    [RelayCommand]
    private void CancelTools() => _toolsCts?.Cancel();

    private bool CanAdd() =>
        TryParse(Url, out _) && Folder.Trim().Length > 0 && Path.IsPathFullyQualified(Folder.Trim()) && !IsSettingUpTools
        && (!IsVideoMode || (SelectedQuality is { } q && (!q.IsCustom || SelectedFormat is not null)));

    private void Accept(bool start)
    {
        Abandon();
        if (!TryParse(Url, out var uri)) return;
        var name = string.IsNullOrWhiteSpace(FileName) ? "" : FileNames.Sanitize(FileName);
        Result = IsVideoMode && _video is not null && _videoUrl is not null
            ? VideoResult()
            : new DownloadInfo
            {
                Url = uri.ToString(),
                FileName = name,
                Directory = Folder.Trim(),
                Category = Category,
                Connections = (int)Math.Clamp(Connections ?? 8, 1, 32),
                Headers = new Dictionary<string, string>(_headers),
                ExpectFile = _hostPage,
                // After the browser finds the file: a name the user typed stays, otherwise the server names it.
                RequestedFileName = _hostPage ? (_nameEdited ? name : "") : null,
            };
        StartImmediately = start;
        CloseRequested?.Invoke();
    }

    private DownloadInfo VideoResult()
    {
        var video = _video!;
        var custom = SelectedQuality?.IsCustom == true ? SelectedFormat?.Format : null;
        var audio = IsAudioResult;
        var container = SelectedContainer == "mkv" ? VideoContainer.Mkv : VideoContainer.Mp4;
        var name = OutputName(FileName, video.Title);
        // Only a display name until yt-dlp reports the real file.
        var ext = custom is { IsAudioOnly: true } ? custom.Ext ?? "m4a"
            : audio ? (container == VideoContainer.Mp4 ? "m4a" : "opus")
            : container == VideoContainer.Mp4 ? "mp4" : "mkv";

        return new DownloadInfo
        {
            Url = _videoUrl!,
            Kind = DownloadKind.Video,
            FileName = $"{name}.{ext}",
            Directory = Folder.Trim(),
            Category = Category,
            Headers = new Dictionary<string, string>(_headers),
            Video = new VideoOptions
            {
                Quality = custom is not null
                    ? custom.IsAudioOnly ? VideoQuality.AudioOnly : VideoQuality.Best
                    : SelectedQuality?.Quality ?? VideoQuality.Best,
                FormatIds = custom is null ? null : custom.IsVideoOnly ? [custom.Id, "ba"] : [custom.Id],
                Container = container,
                OutputName = name,
                Title = video.Title,
                Thumbnail = video.Thumbnail,
                DurationSeconds = video.Duration?.TotalSeconds,
            },
        };
    }

    /// <summary>The name field without a video/audio extension the user may have typed; yt-dlp adds the real one.</summary>
    private static string OutputName(string typed, string title)
    {
        var name = string.IsNullOrWhiteSpace(typed) ? title : typed.Trim();
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext is "mp4" or "mkv" or "webm" or "m4a" or "opus" or "mp3") name = Path.GetFileNameWithoutExtension(name);
        return FileNames.Sanitize(name);
    }

    private async Task ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            await Task.Delay(350, ct); // let typing settle
            _hostPage = false;
            IsChecking = true;
            SetNote("Checking the link…", NoteKind.Neutral);
            ProbeResult probe;
            try
            {
                probe = await UrlProbe.ProbeAsync(manager.Http, url, _headers, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Some video sites refuse plain requests; yt-dlp may still get through. Only worth it if it's installed.
                if (videoTools.IsInstalled && await TryVideoAsync(url, null, ct)) return;
                if (ct.IsCancellationRequested) return;
                if (ex is ServerRefusedException { MayNeedBrowser: true } refused)
                {
                    // File hosts often refuse anything that isn't a browser; one may still get through.
                    _hostPage = true;
                    SetNote($"The host refused TurboDL's request (HTTP {refused.StatusCode}). TurboDL will open the link in a " +
                            "browser and click through to the file. If the link is gone, just close the browser.", NoteKind.Warning);
                    return;
                }
                SetNote($"Couldn't check the link: {ex.GetBaseException().Message} You can still add it and TurboDL will retry.",
                    NoteKind.Error);
                return;
            }
            if (ct.IsCancellationRequested) return;

            if (!_nameEdited) SetQuietly(() => FileName = probe.FileName);
            UpdateFolder();
            var (fileNote, fileKind) = FileNote(url, probe);
            SetNote(fileNote, fileKind);

            // A web page rather than a file: ask yt-dlp whether there's a video on it.
            if (IsPage(probe.ContentType))
            {
                if (await TryVideoAsync(url, null, ct)) return;
                if (ct.IsCancellationRequested || IsVideoMode) return;
                // No video: a file host's waiting page. The page's own name (often "archive.rar.html") isn't the file's.
                _hostPage = UrlProbe.IsWebPage(probe.ContentType);
                if (_hostPage && !_nameEdited) SetQuietly(() => FileName = WithoutPageExtension(FileName));
                // Only the "looking for a video" status gives way; a warning about setup stays.
                if (NoteKind == NoteKind.Neutral)
                {
                    if (_hostPage) SetNote(HostPageNote, NoteKind.Neutral);
                    else SetNote(fileNote, fileKind);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsChecking = false;
        }
    }

    /// <summary>addVideo: yt-dlp first; when it finds nothing (or setup is cancelled), the link is a plain file after all.</summary>
    private async Task VideoFirstAsync(string url, CancellationToken ct)
    {
        try
        {
            if (await TryVideoAsync(url, _pageUrl, ct) || ct.IsCancellationRequested) return;
            if (NoteKind == NoteKind.Error) return; // setup failed: its message stays
            if (_skipVideoSetup)
            {
                // Setup cancelled: carry on as a normal link.
                await ProbeAsync(url, ct);
                return;
            }
            SetNote("yt-dlp found no video here. Adding it downloads the link as a plain file.", NoteKind.Warning);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsChecking = false;
        }
    }

    /// <summary>
    /// Asks yt-dlp about <paramref name="url"/> (then <paramref name="alternate"/>), setting the tools up first if
    /// needed. True when it found a video (now in video mode) or reported an error (now in the note).
    /// </summary>
    private async Task<bool> TryVideoAsync(string url, string? alternate, CancellationToken ct)
    {
        if (!videoTools.IsInstalled && (_skipVideoSetup || !await SetUpToolsAsync(ct))) return false;

        IsChecking = true;
        SetNote("Looking for a video on this page…", NoteKind.Neutral);
        foreach (var candidate in new[] { url, alternate }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await videoTools.Service.ProbeAsync(candidate, _headers, ct);
            if (ct.IsCancellationRequested) return true;
            if (result is null) continue;
            if (result.Error is { } error)
            {
                // yt-dlp's own words (playlist, geo block, login, DRM…).
                SetNote(error.Message, NoteKind.Error);
                return true;
            }
            EnterVideoMode(candidate, result.Info!);
            return true;
        }
        return false;
    }

    /// <summary>Downloads yt-dlp, ffmpeg and Deno with progress. False when cancelled or failed (the link stays a file).</summary>
    private async Task<bool> SetUpToolsAsync(CancellationToken ct)
    {
        _toolsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _toolsCts.Token;
        IsSettingUpTools = true;
        ToolsIndeterminate = true;
        ToolsFraction = 0;
        ToolsText = $"Setting up video support: yt-dlp, ffmpeg and Deno ({VideoToolsService.SetupSizeText}, once)…";
        SetNote("This link looks like a video page. TurboDL needs its video tools first.", NoteKind.Neutral);
        try
        {
            // Progress<T> posts back to this (UI) thread.
            var progress = new Progress<ToolSetupProgress>(p =>
            {
                if (token.IsCancellationRequested) return;
                ToolsText = $"Setting up video support ({VideoToolsService.SetupSizeText}, once). {VideoToolsService.Describe(p)}";
                ToolsIndeterminate = p.Stage != ToolSetupStage.Downloading || p.TotalBytes is not > 0;
                ToolsFraction = p.TotalBytes is > 0 ? (double)p.BytesReceived / p.TotalBytes.Value : 0;
            });
            await videoTools.EnsureAsync(progress, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _skipVideoSetup = true;
            if (!ct.IsCancellationRequested)
                SetNote("Video support wasn't set up, so this link downloads as a plain file. It carries on where it stopped next time.",
                    NoteKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _skipVideoSetup = true;
            SetNote($"Couldn't set up video support: {ex.GetBaseException().Message}", NoteKind.Error);
            return false;
        }
        finally
        {
            IsSettingUpTools = false;
        }
    }

    private void EnterVideoMode(string url, VideoInfo info)
    {
        _videoUrl = url;
        _video = info;
        VideoTitle = info.Title;
        VideoMeta = string.Join(" · ", new[]
        {
            info.Duration is { } d ? Format.Duration(d) : null,
            info.Extractor is { Length: > 0 } x && x != "generic" ? char.ToUpperInvariant(x[0]) + x[1..] : null,
            info.Uploader,
        }.Where(s => !string.IsNullOrEmpty(s)));
        Thumbnail = info.Thumbnail;

        Formats.Clear();
        // Best first: yt-dlp lists worst to best.
        foreach (var f in info.Formats.Reverse()) Formats.Add(FormatChoice.From(f));
        SelectedFormat = null;
        SelectedQuality = Qualities[0];

        if (!_nameEdited) SetQuietly(() => FileName = FileNames.Sanitize(info.Title));
        IsVideoMode = true;
        UpdateFolder();

        var duplicate = manager.Items.Any(i => string.Equals(i.Info.Url, url, StringComparison.OrdinalIgnoreCase));
        SetNote(duplicate ? "Video found. This link is already in your list." : "Video found. Pick a quality below.",
            duplicate ? NoteKind.Warning : NoteKind.Neutral);
    }

    private void ExitVideoMode()
    {
        if (!IsVideoMode && _video is null) return;
        IsVideoMode = false;
        _video = null;
        _videoUrl = null;
        Thumbnail = null;
        VideoTitle = VideoMeta = "";
        Formats.Clear();
        SelectedFormat = null;
        SelectedQuality = null;
    }

    private (string, NoteKind) FileNote(string url, ProbeResult probe)
    {
        var size = probe.Length is long len ? Format.Bytes(len) : "Size unknown";
        var resumable = probe.SupportsRanges && probe.Length is not null;
        var duplicate = manager.Items.Any(i => string.Equals(i.Info.Url, url, StringComparison.OrdinalIgnoreCase));
        if (duplicate) return ($"{size}. This link is already in your list.", NoteKind.Warning);
        if (resumable) return ($"{size}, can pause and resume", NoteKind.Neutral);
        return ($"{size}. The server doesn't allow resuming, so this downloads on one connection.", NoteKind.Warning);
    }

    /// <summary>"archive.rar.html" → "archive.rar", "abc123.htm" → "abc123"; other names as they are.</summary>
    private static string WithoutPageExtension(string name) =>
        name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;

    private static bool IsPage(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase));

    private void UpdateFolder()
    {
        // Video mode files by what comes out: the Video category, or Music for audio only.
        Category = IsVideoMode ? rules.CategoryFor(IsAudioResult ? "a.m4a" : "v.mp4") : rules.CategoryFor(FileName);
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
