namespace TurboDL.Core.Video;

/// <summary>Builds yt-dlp command lines. Kept apart from the process code so the exact arguments can be tested.</summary>
public static class YtDlpArguments
{
    /// <summary>Marks yt-dlp's progress lines on stdout; the rest is JSON (see <see cref="ProgressTemplate"/>).</summary>
    public const string ProgressPrefix = "[turbodl:dl]";

    /// <summary>Marks post-processing lines (merging, remuxing).</summary>
    public const string PostProcessPrefix = "[turbodl:pp]";

    /// <summary>Marks the line with the finished file's path.</summary>
    public const string FilePrefix = "[turbodl:file]";

    public const string ProgressTemplate =
        "download:" + ProgressPrefix +
        "{\"status\":%(progress.status)j,\"downloaded\":%(progress.downloaded_bytes)j,\"total\":%(progress.total_bytes)j," +
        "\"estimate\":%(progress.total_bytes_estimate)j,\"speed\":%(progress.speed)j,\"eta\":%(progress.eta)j," +
        "\"format\":%(info.format_id)j,\"vcodec\":%(info.vcodec)j,\"acodec\":%(info.acodec)j}";

    public const string PostProcessTemplate =
        "postprocess:" + PostProcessPrefix + """{"status":%(progress.status)j,"pp":%(progress.postprocessor)j}""";

    public const string FilePrintTemplate = "after_move:" + FilePrefix + "%(filepath)s";

    /// <summary>Default file name: the title, capped at 150 bytes so long titles stay under Windows' path limits.</summary>
    public const string TitleTemplate = "%(title).150B.%(ext)s";

    /// <param name="cookiesFile">A Netscape cookies file, or null. The cookie values themselves never appear in the arguments.</param>
    /// <param name="denoPath">deno.exe, when installed: yt-dlp's JavaScript runtime for YouTube.</param>
    public static List<string> Probe(string url, IReadOnlyDictionary<string, string>? headers, string? cookiesFile,
        string? denoPath, bool allowGenericExtractor)
    {
        var args = Common(denoPath);
        args.Add("-J");
        if (!allowGenericExtractor)
        {
            args.Add("--ies");
            args.Add("default,-generic");
        }
        AddHeaders(args, headers, cookiesFile);
        AddUrl(args, url);
        return args;
    }

    public static List<string> Download(VideoDownloadRequest request, string ffmpegDirectory, string? cookiesFile, string? denoPath)
    {
        var args = Common(denoPath);
        args.AddRange(["--ffmpeg-location", ffmpegDirectory]);
        AddFormat(args, request);

        args.AddRange(["-P", request.OutputDirectory]);
        args.AddRange(["-o", OutputTemplate(request.FileName)]);
        // The file's date is when it was downloaded, like every other download in the list.
        args.Add("--no-mtime");

        // --print implies --quiet; --progress brings the progress lines back.
        args.AddRange(["--newline", "--progress"]);
        args.AddRange(["--progress-template", ProgressTemplate]);
        args.AddRange(["--progress-template", PostProcessTemplate]);
        args.AddRange(["--print", FilePrintTemplate]);

        if (request.Resume) args.Add("--continue");
        if (request.RateLimit is > 0 and var limit) args.AddRange(["--limit-rate", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        AddHeaders(args, request.Headers, cookiesFile);
        AddUrl(args, request.Url);
        return args;
    }

    private static List<string> Common(string? denoPath)
    {
        // A yt-dlp.conf the user keeps for their own use must not change what TurboDL asks for.
        List<string> args = ["--ignore-config", "--no-playlist", "--encoding", "utf-8"];
        if (denoPath is not null) args.AddRange(["--js-runtimes", "deno:" + denoPath]);
        return args;
    }

    private static void AddFormat(List<string> args, VideoDownloadRequest request)
    {
        var mp4 = request.Container == VideoContainer.Mp4;
        var container = mp4 ? "mp4" : "mkv";

        if (request.FormatIds is { Count: > 0 } ids)
        {
            args.AddRange(["-f", string.Join('+', ids)]);
            args.AddRange(["--merge-output-format", container, "--remux-video", container]);
            return;
        }

        if (request.Quality == VideoQuality.AudioOnly)
        {
            // Best audio stream as-is (no re-encode); mp4 prefers m4a.
            args.AddRange(["-f", "ba/b", "-x"]);
            if (mp4) args.AddRange(["-S", "ext:mp4:m4a"]);
            return;
        }

        args.AddRange(["-f", "bv*+ba/b"]);
        // res:N prefers the largest resolution up to N and falls back to the smallest above it if nothing fits.
        var sort = request.Quality switch
        {
            VideoQuality.Max1080 => "res:1080",
            VideoQuality.Max720 => "res:720",
            _ => null,
        };
        if (mp4) sort = sort is null ? "res,ext:mp4:m4a" : sort + ",ext:mp4:m4a";
        if (sort is not null) args.AddRange(["-S", sort]);
        args.AddRange(["--merge-output-format", container, "--remux-video", container]);
    }

    private static string OutputTemplate(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return TitleTemplate;
        // '%' would start a template field; the name is literal.
        return Engine.FileNames.Sanitize(fileName).Replace("%", "%%") + ".%(ext)s";
    }

    private static void AddHeaders(List<string> args, IReadOnlyDictionary<string, string>? headers, string? cookiesFile)
    {
        if (cookiesFile is not null) args.AddRange(["--cookies", cookiesFile]);
        if (headers is null) return;
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue; // Only ever via the cookies file.
            if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) args.AddRange(["--referer", value.Trim()]);
            else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) args.AddRange(["--user-agent", value.Trim()]);
            else if (!IsManagedHeader(name)) args.AddRange(["--add-headers", $"{name.Trim()}:{value.Trim()}"]);
        }
    }

    // Headers that would break yt-dlp's own requests, or leak credentials onto the command line.
    private static bool IsManagedHeader(string name) =>
        name.Equals("Range", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private static void AddUrl(List<string> args, string url)
    {
        // "--" ends the options, so a URL starting with '-' can never be read as one.
        args.Add("--");
        args.Add(url.Trim());
    }
}
