namespace TurboDL.Core.Video;

/// <summary>What yt-dlp found on a video page (<c>yt-dlp -J</c>).</summary>
public sealed class VideoInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>A thumbnail URL, preferring JPEG/PNG over WebP (which WPF can't show).</summary>
    public string? Thumbnail { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>yt-dlp's extractor name, e.g. "youtube" or "generic".</summary>
    public string? Extractor { get; init; }

    public string? Uploader { get; init; }
    public string? WebpageUrl { get; init; }

    /// <summary>Downloadable formats, worst to best as yt-dlp lists them. Storyboards and other image-only entries are left out.</summary>
    public IReadOnlyList<VideoFormat> Formats { get; init; } = [];
}

public sealed class VideoFormat
{
    public required string Id { get; init; }
    public string? Ext { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>yt-dlp's own label, e.g. "1920x1080" or "audio only".</summary>
    public string? Resolution { get; init; }

    public double? Fps { get; init; }

    /// <summary>Video codec; "none" for audio-only, null when yt-dlp doesn't know.</summary>
    public string? VCodec { get; init; }

    /// <summary>Audio codec; "none" for video-only, null when yt-dlp doesn't know.</summary>
    public string? ACodec { get; init; }

    /// <summary>Exact size in bytes, when the site says.</summary>
    public long? FileSize { get; init; }

    /// <summary>yt-dlp's estimate from bitrate × duration, when the exact size isn't known.</summary>
    public long? FileSizeApprox { get; init; }

    /// <summary>Total bitrate in kbit/s.</summary>
    public double? Tbr { get; init; }

    public string? Note { get; init; }
    public string? Protocol { get; init; }

    public bool IsAudioOnly => VCodec == "none" && ACodec != "none";
    public bool IsVideoOnly => ACodec == "none" && VCodec != "none";

    /// <summary>The exact size if known, otherwise the estimate.</summary>
    public long? Size => FileSize ?? FileSizeApprox;
}

public enum YtDlpErrorKind
{
    /// <summary>yt-dlp has no extractor for this URL (or it's not a video page).</summary>
    Unsupported,
    Network,
    GeoRestricted,
    Drm,
    /// <summary>Needs a login, cookies, membership or age confirmation.</summary>
    LoginRequired,
    /// <summary>Removed, private, or not found.</summary>
    Unavailable,
    /// <summary>The link is a playlist; only single videos are supported.</summary>
    Playlist,
    /// <summary>yt-dlp didn't answer in time.</summary>
    Timeout,
    /// <summary>yt-dlp.exe or ffmpeg isn't installed yet; call <see cref="YtDlpTools.EnsureAsync"/>.</summary>
    ToolsMissing,
    Other,
}

/// <summary>A failure reported by yt-dlp. <see cref="Message"/> is yt-dlp's own text, unchanged.</summary>
public sealed record YtDlpError(YtDlpErrorKind Kind, string Message, int? ExitCode = null);

/// <summary>
/// The outcome of a probe that found something: either the video's details or an error worth showing.
/// (A URL yt-dlp doesn't support is not a result at all: <see cref="YtDlpService.ProbeAsync"/> returns null.)
/// </summary>
public sealed class VideoProbeResult
{
    public VideoInfo? Info { get; }
    public YtDlpError? Error { get; }

    public bool IsSuccess => Info is not null;

    private VideoProbeResult(VideoInfo? info, YtDlpError? error)
    {
        Info = info;
        Error = error;
    }

    public static VideoProbeResult Success(VideoInfo info) => new(info, null);

    public static VideoProbeResult Failure(YtDlpError error) => new(null, error);
}

/// <summary>Ready-made format choices. Explicit format ids in <see cref="VideoDownloadRequest.FormatIds"/> win over these.</summary>
public enum VideoQuality
{
    Best,
    Max1080,
    Max720,
    AudioOnly,
}

public enum VideoContainer
{
    Mp4,
    Mkv,
}

public sealed class VideoDownloadRequest
{
    /// <summary>The video page URL.</summary>
    public required string Url { get; init; }

    /// <summary>The folder the finished file goes to.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>File name without extension. Null = the video's title.</summary>
    public string? FileName { get; init; }

    public VideoQuality Quality { get; init; } = VideoQuality.Best;

    /// <summary>
    /// Explicit yt-dlp formats (e.g. ["137", "140"], or ["137", "ba"] for the best audio), merged in that order.
    /// They replace the preset's choice; with <see cref="VideoQuality.AudioOnly"/> the result is kept as audio.
    /// </summary>
    public IReadOnlyList<string>? FormatIds { get; init; }

    /// <summary>The container video is merged/remuxed into. Ignored for <see cref="VideoQuality.AudioOnly"/>, except that mp4 prefers m4a audio.</summary>
    public VideoContainer Container { get; init; } = VideoContainer.Mp4;

    /// <summary>Browser headers: Cookie (passed via a temporary cookies file), Referer, User-Agent, and any others.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Speed cap in bytes/second, passed to yt-dlp's <c>--limit-rate</c>. Null or 0 = unlimited.</summary>
    public long? RateLimit { get; init; }

    /// <summary>Carry on from yt-dlp's .part files left by an earlier, stopped run.</summary>
    public bool Resume { get; init; }
}

public enum VideoPhase
{
    /// <summary>Downloading a stream that has both video and audio, or one yt-dlp didn't label.</summary>
    Downloading,
    DownloadingVideo,
    DownloadingAudio,
    /// <summary>ffmpeg is merging the video and audio streams.</summary>
    Merging,
    /// <summary>Another post-processing step: remuxing, extracting audio, fixing up.</summary>
    Processing,
}

public sealed record VideoProgress
{
    public VideoPhase Phase { get; init; }

    /// <summary>The yt-dlp format being downloaded, e.g. "137".</summary>
    public string? FormatId { get; init; }

    /// <summary>Bytes of the current stream so far.</summary>
    public long DownloadedBytes { get; init; }

    /// <summary>Size of the current stream: exact, or yt-dlp's estimate (see <see cref="IsEstimate"/>).</summary>
    public long? TotalBytes { get; init; }

    public bool IsEstimate { get; init; }

    /// <summary>Bytes/second.</summary>
    public double? Speed { get; init; }

    public TimeSpan? Eta { get; init; }

    /// <summary>The post-processor yt-dlp is running, e.g. "Merger" (merging and processing phases only).</summary>
    public string? PostProcessor { get; init; }

    /// <summary>The file this stream is being written to (e.g. "Title.f137.mp4"), when yt-dlp says.</summary>
    public string? FileName { get; init; }

    /// <summary>The partial file while it downloads (e.g. "Title.f137.mp4.part"), when yt-dlp says.</summary>
    public string? TempFileName { get; init; }
}

public sealed record VideoDownloadResult(string FilePath);

/// <summary>A video download stopped with an error yt-dlp reported (or yt-dlp couldn't be run).</summary>
public sealed class VideoDownloadException(YtDlpError error)
    : Engine.DownloadFailedException(error.Message)
{
    public YtDlpError Error { get; } = error;
}
