using System.Text;

namespace TurboDL.Core.Video;

/// <summary>
/// Video pages through yt-dlp: <see cref="ProbeAsync"/> lists what's there, <see cref="DownloadAsync"/> fetches and
/// merges it with ffmpeg. Pausing is cancelling (the process tree is killed); resuming is a new run with
/// <see cref="VideoDownloadRequest.Resume"/>, which picks up yt-dlp's .part files.
/// </summary>
public sealed class YtDlpService(YtDlpTools tools)
{
    public YtDlpTools Tools { get; } = tools;

    /// <summary>How long a probe may take before it's given up (default 15 s).</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Let yt-dlp's generic extractor look at pages no site extractor claims (direct video links, HTML5 video).
    /// On by default; off limits probes to known sites.
    /// </summary>
    public bool AllowGenericExtractor { get; init; } = true;

    /// <summary>
    /// Asks yt-dlp what's on <paramref name="url"/>. Returns null when yt-dlp doesn't support the URL (the
    /// Add dialog uses this to tell video pages from plain files), otherwise the video's details or a typed error
    /// carrying yt-dlp's message unchanged.
    /// </summary>
    public async Task<VideoProbeResult?> ProbeAsync(string url, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(Tools.YtDlpPath)) return VideoProbeResult.Failure(ToolsMissing());

        string? cookiesFile = null;
        try
        {
            cookiesFile = WriteCookies(url, headers);
            var args = YtDlpArguments.Probe(url, headers, cookiesFile, DenoOrNull(), AllowGenericExtractor);
            var stdout = new StringBuilder();
            ToolRunResult run;
            try
            {
                run = await ToolProcess.RunAsync(Tools.YtDlpPath, args, line => stdout.AppendLine(line), null, ProbeTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return VideoProbeResult.Failure(CouldNotStart(ex));
            }

            if (run.TimedOut)
                return VideoProbeResult.Failure(new YtDlpError(YtDlpErrorKind.Timeout,
                    $"yt-dlp didn't answer within {ProbeTimeout.TotalSeconds:0} seconds."));

            if (run.ExitCode != 0)
            {
                var error = YtDlpOutput.ToError(run.StdErr, run.ExitCode);
                return error.Kind == YtDlpErrorKind.Unsupported ? null : VideoProbeResult.Failure(error);
            }

            var json = stdout.ToString().Trim();
            if (json.Length == 0)
                return VideoProbeResult.Failure(new YtDlpError(YtDlpErrorKind.Other,
                    YtDlpOutput.ErrorMessage(run.StdErr, run.ExitCode), run.ExitCode));
            try
            {
                return YtDlpOutput.ParseInfo(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return VideoProbeResult.Failure(new YtDlpError(YtDlpErrorKind.Other, "yt-dlp returned output TurboDL couldn't read."));
            }
        }
        finally
        {
            NetscapeCookies.DeleteQuietly(cookiesFile);
        }
    }

    /// <summary>
    /// Downloads the video and returns where the finished file is. Cancelling kills yt-dlp and ffmpeg and throws
    /// <see cref="OperationCanceledException"/>; the .part files stay for a later run with <c>Resume = true</c>.
    /// Failures throw <see cref="VideoDownloadException"/> with yt-dlp's message unchanged.
    /// </summary>
    public async Task<VideoDownloadResult> DownloadAsync(VideoDownloadRequest request, IProgress<VideoProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Tools.IsInstalled) throw new VideoDownloadException(ToolsMissing());
        Directory.CreateDirectory(request.OutputDirectory);

        string? cookiesFile = null;
        try
        {
            cookiesFile = WriteCookies(request.Url, request.Headers);
            var args = YtDlpArguments.Download(request, Tools.Directory, cookiesFile, DenoOrNull());
            string? filePath = null;

            void OnLine(string line)
            {
                if (YtDlpOutput.ParseProgress(line) is { } p) progress?.Report(p);
                else if (YtDlpOutput.ParsePostProcess(line) is { } pp) progress?.Report(pp);
                else if (YtDlpOutput.ParseFilePath(line) is { } path) filePath = path;
            }

            ToolRunResult run;
            try
            {
                // --print makes yt-dlp quiet, and a quiet yt-dlp writes its progress to stderr; the file path comes on stdout.
                run = await ToolProcess.RunAsync(Tools.YtDlpPath, args, OnLine, OnLine, timeout: null, ct).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new VideoDownloadException(CouldNotStart(ex));
            }
            if (run.ExitCode != 0) throw new VideoDownloadException(YtDlpOutput.ToError(run.StdErr, run.ExitCode));
            if (filePath is null)
                throw new VideoDownloadException(new YtDlpError(YtDlpErrorKind.Other,
                    "yt-dlp finished but didn't say where it saved the file.", run.ExitCode));
            return new VideoDownloadResult(filePath);
        }
        finally
        {
            NetscapeCookies.DeleteQuietly(cookiesFile);
        }
    }

    private string? DenoOrNull() => File.Exists(Tools.DenoPath) ? Tools.DenoPath : null;

    private static YtDlpError ToolsMissing() =>
        new(YtDlpErrorKind.ToolsMissing, "The video tools (yt-dlp, ffmpeg, Deno) aren't installed yet.");

    private static YtDlpError CouldNotStart(System.ComponentModel.Win32Exception ex) =>
        new(YtDlpErrorKind.Other, $"Couldn't start yt-dlp: {ex.Message}");

    private static string? WriteCookies(string url, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var cookie = headers.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(cookie) ? null : NetscapeCookies.WriteTempFile(cookie, uri);
    }
}
