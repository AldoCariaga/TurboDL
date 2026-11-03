using System.Text.Json.Serialization;

namespace TurboDL.Core.Model;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
}

public enum DownloadKind
{
    /// <summary>Plain HTTP(S) file, handled by the segmented engine.</summary>
    Http,
    /// <summary>Page URL handed to yt-dlp.</summary>
    Video,
}

/// <summary>
/// Everything needed to show, persist and resume one download.
/// This is the object stored in the database (as JSON).
/// </summary>
public sealed class DownloadInfo
{
    public long Id { get; set; }
    public required string Url { get; set; }
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    public string? FinalUrl { get; set; }

    /// <summary>File name only. Empty means "take it from the server".</summary>
    public string FileName { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Category { get; set; } = "Other";

    public long? TotalSize { get; set; }
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>Extra request headers, e.g. Cookie, Referer, User-Agent passed in from the browser.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    public int Connections { get; set; } = 8;

    /// <summary>Per-download speed cap in bytes/second. Null or 0 = unlimited.</summary>
    public long? SpeedLimit { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }

    public List<SegmentState> Segments { get; set; } = [];

    /// <summary>yt-dlp settings and progress for <see cref="DownloadKind.Video"/>; null for plain files.</summary>
    public Video.VideoOptions? Video { get; set; }

    /// <summary>The batch this download belongs to, or null.</summary>
    public long? BatchId { get; set; }

    /// <summary>
    /// The link must give a file, not a web page. When set, a text/html answer fails the download with
    /// HostReturnedPageException instead of saving the page. Set for batch downloads.
    /// Redirects are followed and <see cref="Headers"/> go with them on the same host; when a redirect
    /// moves to another host, .NET may drop credentials such as the Cookie header, which is deliberate.
    /// </summary>
    public bool ExpectFile { get; set; }

    /// <summary>
    /// The name taken from the link when a batch was made, or typed for a file host's page ("" when the server picks it);
    /// null otherwise. It's the name kept when the download restarts on the link a browser found.
    /// <see cref="FileName"/> can't serve, because the engine replaces it with a unique name when the first one is taken.
    /// </summary>
    public string? RequestedFileName { get; set; }

    /// <summary>True when the last failure was a network or server hiccup worth retrying later (not a 404, a web page or a changed file).</summary>
    public bool ErrorIsTransient { get; set; }

    [JsonIgnore] public string FilePath => Path.Combine(Directory, FileName);
    [JsonIgnore] public string TempPath => FilePath + ".turbodl";
    [JsonIgnore] public long Downloaded => Video?.DownloadedBytes ?? Segments.Sum(s => s.Position - s.Start);
}

/// <summary>A byte range of the file and how far it has got. <see cref="End"/> is inclusive.</summary>
public sealed class SegmentState
{
    public long Start { get; set; }

    /// <summary>Inclusive end offset; <see cref="long.MaxValue"/> when the file size is unknown.</summary>
    public long End { get; set; }

    /// <summary>Next byte to write.</summary>
    public long Position { get; set; }
}
