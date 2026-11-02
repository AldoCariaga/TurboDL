using TurboDL.Core.Model;

namespace TurboDL.Core.Video;

/// <summary>What a video download was asked for and how far it got. Saved with its <see cref="DownloadInfo"/>.</summary>
public sealed class VideoOptions
{
    public VideoQuality Quality { get; set; } = VideoQuality.Best;

    /// <summary>Explicit yt-dlp formats (merged in order); overrides <see cref="Quality"/>.</summary>
    public List<string>? FormatIds { get; set; }

    public VideoContainer Container { get; set; } = VideoContainer.Mp4;

    /// <summary>File name without extension; yt-dlp adds the right one.</summary>
    public string OutputName { get; set; } = "";

    public string? Title { get; set; }
    public string? Thumbnail { get; set; }
    public double? DurationSeconds { get; set; }

    /// <summary>A run has begun, so the next one carries on from yt-dlp's partial files (<c>--continue</c>).</summary>
    public bool Started { get; set; }

    /// <summary>Bytes received over all streams so far (for the list after a restart).</summary>
    public long DownloadedBytes { get; set; }

    /// <summary>Files yt-dlp was writing (streams and their .part files), so deleting an unfinished download can clean up.</summary>
    public List<string> PartialFiles { get; set; } = [];

    /// <summary>What's being done right now, e.g. "Downloading audio" or "Merging"; null when idle.</summary>
    public string? PhaseText { get; set; }

    /// <summary>A short description for the UI, e.g. "Best quality · mp4".</summary>
    public string Describe()
    {
        var what = FormatIds is { Count: > 0 } ids
            ? "Format " + string.Join(" + ", ids)
            : Quality switch
            {
                VideoQuality.Max1080 => "Up to 1080p",
                VideoQuality.Max720 => "Up to 720p",
                VideoQuality.AudioOnly => "Audio only",
                _ => "Best quality",
            };
        return Quality == VideoQuality.AudioOnly && FormatIds is not { Count: > 0 }
            ? what
            : $"{what} · {(Container == VideoContainer.Mp4 ? "mp4" : "mkv")}";
    }
}

/// <summary>A point-in-time view of a running video download, read by the UI on its own timer.</summary>
public sealed record VideoJobSnapshot(
    VideoPhase? Phase,
    long Downloaded,
    long? Total,
    bool TotalIsEstimate,
    double BytesPerSecond,
    TimeSpan? Remaining,
    string? PhaseText);

/// <summary>
/// One run of a <see cref="DownloadKind.Video"/> download: drives <see cref="YtDlpService.DownloadAsync"/> and keeps
/// the <see cref="DownloadInfo"/> up to date: Downloading, then Completed (with the real file name), Paused
/// (cancelled) or Failed (with yt-dlp's message). The next run after a pause or a crash resumes with --continue.
/// </summary>
public sealed class VideoJob
{
    private readonly DownloadInfo _info;
    private readonly YtDlpService _service;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Done, long? Total, bool Estimate)> _streams = [];
    private VideoProgress? _last;
    private DateTime _lastCheckpoint = DateTime.MinValue;

    /// <param name="globalLimit">The app-wide speed cap in bytes/second (0 = none), read when the run starts.</param>
    public VideoJob(DownloadInfo info, YtDlpService service, long globalLimit = 0)
    {
        if (info.Kind != DownloadKind.Video || info.Video is null)
            throw new ArgumentException("Not a video download.", nameof(info));
        _info = info;
        _service = service;
        RateLimit = EffectiveLimit(info.SpeedLimit ?? 0, globalLimit);
    }

    /// <summary>Saves the info; raised on a background thread. Thread-safe handlers only.</summary>
    public event Action<DownloadInfo>? Checkpoint;

    /// <summary>The --limit-rate this run uses, bytes/second (0 = none).</summary>
    public long RateLimit { get; }

    /// <summary>
    /// yt-dlp can't share the app's token bucket, so each video download gets the tighter of its own cap and the
    /// global one. 0 = unlimited.
    /// </summary>
    public static long EffectiveLimit(long perDownload, long global) =>
        perDownload > 0 && global > 0 ? Math.Min(perDownload, global) : Math.Max(Math.Max(perDownload, 0), Math.Max(global, 0));

    public VideoJobSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var downloaded = _streams.Values.Sum(s => s.Done);
            long? total = _streams.Count > 0 && _streams.Values.All(s => s.Total is not null)
                ? _streams.Values.Sum(s => s.Total!.Value)
                : null;
            var downloading = _last?.Phase is VideoPhase.Downloading or VideoPhase.DownloadingVideo or VideoPhase.DownloadingAudio;
            return new VideoJobSnapshot(
                _last?.Phase,
                downloaded > 0 ? downloaded : _info.Video!.DownloadedBytes,
                total ?? _info.TotalSize,
                _streams.Values.Any(s => s.Estimate),
                downloading ? _last!.Speed ?? 0 : 0,
                downloading ? _last!.Eta : null,
                _info.Video!.PhaseText);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var video = _info.Video!;
        var request = new VideoDownloadRequest
        {
            Url = _info.Url,
            OutputDirectory = _info.Directory,
            FileName = string.IsNullOrWhiteSpace(video.OutputName) ? null : video.OutputName,
            Quality = video.Quality,
            FormatIds = video.FormatIds,
            Container = video.Container,
            Headers = _info.Headers,
            RateLimit = RateLimit,
            Resume = video.Started,
        };

        lock (_gate)
        {
            _info.Status = DownloadStatus.Downloading;
            _info.Error = null;
            video.Started = true;
            video.PhaseText = "Starting";
        }
        Checkpoint?.Invoke(_info);

        try
        {
            var result = await _service.DownloadAsync(request, new Reporter(this), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _info.Directory = Path.GetDirectoryName(result.FilePath) ?? _info.Directory;
                _info.FileName = Path.GetFileName(result.FilePath);
                var size = File.Exists(result.FilePath) ? new FileInfo(result.FilePath).Length : (long?)null;
                _info.TotalSize = size ?? _info.TotalSize;
                video.DownloadedBytes = size ?? video.DownloadedBytes;
                video.PartialFiles.Clear();
                video.PhaseText = null;
                _info.Status = DownloadStatus.Completed;
                _info.CompletedUtc = DateTime.UtcNow;
            }
            Checkpoint?.Invoke(_info);
        }
        catch (OperationCanceledException)
        {
            Stop(DownloadStatus.Paused, null);
            throw;
        }
        catch (VideoDownloadException ex)
        {
            Stop(DownloadStatus.Failed, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // E.g. the folder can't be created, or yt-dlp couldn't be started.
            Stop(DownloadStatus.Failed, ex.Message);
            throw new VideoDownloadException(new YtDlpError(YtDlpErrorKind.Other, ex.Message));
        }
    }

    private void Stop(DownloadStatus status, string? error)
    {
        lock (_gate)
        {
            _info.Status = status;
            _info.Error = error;
            _info.Video!.PhaseText = null;
            // Streams restart from their .part files, but the sizes stay a useful hint for the list.
            if (_streams.Count > 0) _info.Video.DownloadedBytes = _streams.Values.Sum(s => s.Done);
        }
        Checkpoint?.Invoke(_info);
    }

    private void OnProgress(VideoProgress p)
    {
        var save = false;
        lock (_gate)
        {
            _last = p;
            var video = _info.Video!;
            video.PhaseText = p.Phase switch
            {
                VideoPhase.DownloadingVideo => "Downloading video",
                VideoPhase.DownloadingAudio => "Downloading audio",
                VideoPhase.Merging => "Merging",
                VideoPhase.Processing => "Processing",
                _ => "Downloading",
            };

            if (p.Phase is VideoPhase.Downloading or VideoPhase.DownloadingVideo or VideoPhase.DownloadingAudio)
            {
                var key = p.FormatId ?? p.FileName ?? "";
                _streams[key] = (p.DownloadedBytes, p.TotalBytes, p.IsEstimate);
                video.DownloadedBytes = _streams.Values.Sum(s => s.Done);
                if (_streams.Values.All(s => s.Total is not null)) _info.TotalSize = _streams.Values.Sum(s => s.Total!.Value);
            }

            foreach (var file in new[] { p.FileName, p.TempFileName })
            {
                if (file is null || video.PartialFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
                video.PartialFiles.Add(file);
                save = true;
            }

            if (DateTime.UtcNow - _lastCheckpoint > TimeSpan.FromSeconds(2) || p.Phase is VideoPhase.Merging or VideoPhase.Processing)
                save = true;
            if (save) _lastCheckpoint = DateTime.UtcNow;
        }
        if (save) Checkpoint?.Invoke(_info);
    }

    /// <summary>Reports inline (on yt-dlp's reader thread), so no progress is dropped or reordered.</summary>
    private sealed class Reporter(VideoJob job) : IProgress<VideoProgress>
    {
        public void Report(VideoProgress value) => job.OnProgress(value);
    }

    /// <summary>
    /// Every file an unfinished video download may have left in its folder: the streams yt-dlp named, their .part
    /// files, and its .ytdl resume notes.
    /// </summary>
    public static IEnumerable<string> LeftoverFiles(DownloadInfo info)
    {
        if (info.Video is null || info.Status == DownloadStatus.Completed) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in info.Video.PartialFiles)
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(info.Directory, file);
            var stem = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? path[..^5] : path;
            foreach (var candidate in new[] { stem, stem + ".part", stem + ".ytdl", stem + ".part.ytdl" })
                if (seen.Add(candidate)) yield return candidate;
        }
    }
}
