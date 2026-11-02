using System.Text;
using System.Text.Json;

namespace TurboDL.Core.Video;

/// <summary>Reads what yt-dlp prints: the <c>-J</c> JSON, our progress-template lines, and its error messages.</summary>
public static class YtDlpOutput
{
    /// <summary>
    /// Makes a template line valid JSON. yt-dlp writes a missing %(field)j as a bare NA, which isn't JSON; those
    /// become null. Text inside JSON strings (titles, file names) is never touched: the scan follows quotes and
    /// backslash escapes, and only an NA token standing on its own as a value is replaced.
    /// </summary>
    public static string FixNa(string json)
    {
        StringBuilder? sb = null;
        var inString = false;
        var copied = 0;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                if (c == '\\') i++; // skip the escaped character, whatever it is
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                continue;
            }
            if (c == 'N' && i + 1 < json.Length && json[i + 1] == 'A'
                && !IsTokenChar(i > 0 ? json[i - 1] : ' ') && !IsTokenChar(i + 2 < json.Length ? json[i + 2] : ' '))
            {
                sb ??= new StringBuilder(json.Length + 16);
                sb.Append(json, copied, i - copied).Append("null");
                copied = i + 2;
                i++;
            }
        }
        return sb is null ? json : sb.Append(json, copied, json.Length - copied).ToString();
    }

    // Letters, digits and the like would make "NA" part of a longer token (NaN, NAN, NA1), which isn't ours to touch.
    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '+';

    public const string PlaylistMessage = "This link is a playlist; only single videos are supported for now.";

    /// <summary>Parses <c>yt-dlp -J</c> output. A playlist comes back as a <see cref="YtDlpErrorKind.Playlist"/> error.</summary>
    public static VideoProbeResult ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return VideoProbeResult.Failure(new YtDlpError(YtDlpErrorKind.Other, "yt-dlp returned unexpected output."));

        var type = Str(root, "_type");
        if (type is "playlist" or "multi_video")
            return VideoProbeResult.Failure(new YtDlpError(YtDlpErrorKind.Playlist, PlaylistMessage));

        var formats = new List<VideoFormat>();
        if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                var format = ParseFormat(f);
                if (format is not null) formats.Add(format);
            }
        }
        else if (Str(root, "format_id") is not null)
        {
            // Some extractors describe a single format at the top level instead of a list.
            if (ParseFormat(root) is { } single) formats.Add(single);
        }

        var duration = Num(root, "duration");
        var info = new VideoInfo
        {
            Id = Str(root, "id") ?? "",
            Title = Str(root, "title") ?? Str(root, "id") ?? "video",
            Thumbnail = PickThumbnail(root),
            Duration = duration is >= 0 and < 1e9 ? TimeSpan.FromSeconds(duration.Value) : null,
            Extractor = Str(root, "extractor_key") is { } key ? key.ToLowerInvariant() : Str(root, "extractor"),
            Uploader = Str(root, "uploader"),
            WebpageUrl = Str(root, "webpage_url"),
            Formats = formats,
        };
        return VideoProbeResult.Success(info);
    }

    private static string? PickThumbnail(JsonElement root)
    {
        static bool IsWebp(string url) => url.Split('?')[0].EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

        var main = Str(root, "thumbnail");
        if (main is not null && !IsWebp(main)) return main;
        if (root.TryGetProperty("thumbnails", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            // yt-dlp lists them worst to best; take the best one that isn't WebP.
            foreach (var t in list.EnumerateArray().Reverse())
                if (Str(t, "url") is { } url && !IsWebp(url)) return url;
        }
        return main;
    }

    private static VideoFormat? ParseFormat(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object || Str(f, "format_id") is not { } id) return null;
        var vcodec = Str(f, "vcodec");
        var acodec = Str(f, "acodec");
        // Storyboards (thumbnail sheets) have neither video nor audio.
        if (vcodec == "none" && acodec == "none") return null;
        if (Str(f, "ext") == "mhtml") return null;
        return new VideoFormat
        {
            Id = id,
            Ext = Str(f, "ext"),
            Width = Int(f, "width"),
            Height = Int(f, "height"),
            Resolution = Str(f, "resolution"),
            Fps = Num(f, "fps"),
            VCodec = vcodec,
            ACodec = acodec,
            FileSize = Long(f, "filesize"),
            FileSizeApprox = Long(f, "filesize_approx"),
            Tbr = Num(f, "tbr"),
            Note = Str(f, "format_note"),
            Protocol = Str(f, "protocol"),
        };
    }

    /// <summary>Parses a <see cref="YtDlpArguments.ProgressTemplate"/> line; null for any other line.</summary>
    public static VideoProgress? ParseProgress(string line)
    {
        var json = After(line, YtDlpArguments.ProgressPrefix);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(FixNa(json));
            var p = doc.RootElement;
            var vcodec = Str(p, "vcodec");
            var acodec = Str(p, "acodec");
            var phase = vcodec == "none" && acodec is not null && acodec != "none" ? VideoPhase.DownloadingAudio
                : acodec == "none" && vcodec is not null && vcodec != "none" ? VideoPhase.DownloadingVideo
                : VideoPhase.Downloading;
            var exact = Long(p, "total");
            var estimate = Long(p, "estimate");
            var eta = Num(p, "eta");
            return new VideoProgress
            {
                Phase = phase,
                FormatId = Str(p, "format"),
                DownloadedBytes = Long(p, "downloaded") ?? 0,
                TotalBytes = exact ?? estimate,
                IsEstimate = exact is null && estimate is not null,
                Speed = Num(p, "speed"),
                Eta = eta is >= 0 and < 1e9 ? TimeSpan.FromSeconds(eta.Value) : null,
                FileName = Str(p, "file") is { } file and not "NA" ? file : null,
                TempFileName = Str(p, "tmp") is { } tmp and not "NA" ? tmp : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a <see cref="YtDlpArguments.PostProcessTemplate"/> line when a step starts; null otherwise.</summary>
    public static VideoProgress? ParsePostProcess(string line)
    {
        var json = After(line, YtDlpArguments.PostProcessPrefix);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(FixNa(json));
            var p = doc.RootElement;
            if (Str(p, "status") != "started") return null;
            var pp = Str(p, "pp");
            return new VideoProgress
            {
                Phase = pp == "Merger" ? VideoPhase.Merging : VideoPhase.Processing,
                PostProcessor = pp,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The finished file's path from the <see cref="YtDlpArguments.FilePrintTemplate"/> line; null for any other line.</summary>
    public static string? ParseFilePath(string line)
    {
        var path = After(line, YtDlpArguments.FilePrefix)?.Trim();
        return string.IsNullOrEmpty(path) || path == "NA" ? null : path;
    }

    /// <summary>yt-dlp's error text as-is: its ERROR lines, or the last stderr lines when there are none.</summary>
    public static string ErrorMessage(IReadOnlyList<string> stderr, int exitCode)
    {
        var errors = stderr.Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
        if (errors.Count > 0) return string.Join(Environment.NewLine, errors);
        var tail = stderr.Where(l => !string.IsNullOrWhiteSpace(l) && !IsOurs(l)).TakeLast(5).ToList();
        return tail.Count > 0 ? string.Join(Environment.NewLine, tail) : $"yt-dlp exited with code {exitCode}.";
    }

    /// <summary>Our own progress/post-process/file lines, which yt-dlp may write to stderr among its messages.</summary>
    private static bool IsOurs(string line) =>
        line.Contains(YtDlpArguments.ProgressPrefix, StringComparison.Ordinal)
        || line.Contains(YtDlpArguments.PostProcessPrefix, StringComparison.Ordinal)
        || line.Contains(YtDlpArguments.FilePrefix, StringComparison.Ordinal);

    public static bool IsUnsupported(string message) =>
        message.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sorts a yt-dlp error into a kind the UI can act on. The message itself is kept unchanged.</summary>
    public static YtDlpErrorKind Classify(string message)
    {
        var m = message.ToLowerInvariant();
        if (IsUnsupported(message)) return YtDlpErrorKind.Unsupported;
        if (m.Contains("drm")) return YtDlpErrorKind.Drm;
        if (m.Contains("in your country") || m.Contains("geo restrict") || m.Contains("geo-restrict")
            || m.Contains("geoblock") || m.Contains("not available from your location"))
            return YtDlpErrorKind.GeoRestricted;
        if (m.Contains("sign in") || m.Contains("log in") || m.Contains("login") || m.Contains("--cookies")
            || m.Contains("private video") || m.Contains("members-only") || m.Contains("members only")
            || m.Contains("authentication") || m.Contains("http error 401") || m.Contains("http error 403"))
            return YtDlpErrorKind.LoginRequired;
        if (m.Contains("video unavailable") || m.Contains("is unavailable") || m.Contains("has been removed")
            || m.Contains("http error 404") || m.Contains("http error 410") || m.Contains("does not exist"))
            return YtDlpErrorKind.Unavailable;
        if (m.Contains("unable to download") || m.Contains("urlopen error") || m.Contains("getaddrinfo")
            || m.Contains("timed out") || m.Contains("connection") || m.Contains("network")
            || m.Contains("name resolution") || m.Contains("ssl") || m.Contains("http error 5"))
            return YtDlpErrorKind.Network;
        return YtDlpErrorKind.Other;
    }

    public static YtDlpError ToError(IReadOnlyList<string> stderr, int exitCode)
    {
        var message = ErrorMessage(stderr, exitCode);
        return new YtDlpError(Classify(message), message, exitCode);
    }

    private static string? After(string line, string prefix)
    {
        var at = line.IndexOf(prefix, StringComparison.Ordinal);
        return at < 0 ? null : line[(at + prefix.Length)..];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // yt-dlp prints "NA" (a JSON string) for fields it doesn't have; only real numbers count.
    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? Long(JsonElement e, string name) =>
        Num(e, name) is { } d && d >= 0 && d < 9e18 ? (long)d : null;

    private static int? Int(JsonElement e, string name) =>
        Num(e, name) is { } d && d >= 0 && d < int.MaxValue ? (int)d : null;
}
