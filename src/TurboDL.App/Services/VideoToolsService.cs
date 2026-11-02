using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TurboDL.Core.Video;

namespace TurboDL.App.Services;

/// <summary>
/// The yt-dlp side of the app: one tools folder and service for everything, first-use setup (it starts on its
/// own, with progress and Cancel), and the daily update, which never runs while a video is downloading.
/// </summary>
public sealed class VideoToolsService(YtDlpTools tools, YtDlpService service, ILogger<VideoToolsService> log)
{
    private Task _update = Task.CompletedTask;
    private DispatcherTimer? _timer;
    private Func<bool>? _videoRunning;

    public YtDlpTools Tools => tools;
    public YtDlpService Service => service;
    public bool IsInstalled => tools.IsInstalled;

    /// <summary>Rough first-time download size, for the setup step: yt-dlp 22 MB, ffmpeg 192 MB, Deno 40 MB.</summary>
    public const string SetupSizeText = "about 255 MB";

    /// <summary>Installs whatever is missing. Safe to call from two places at once; the second waits for the first.</summary>
    public Task EnsureAsync(IProgress<ToolSetupProgress>? progress, CancellationToken ct) => tools.EnsureAsync(progress, ct);

    /// <summary>Completes when no update is running. Video downloads wait on this before starting yt-dlp.</summary>
    public Task WaitForUpdateAsync() => _update;

    /// <summary>
    /// Checks for updates a couple of minutes after start and then every hour; <see cref="YtDlpTools.UpdateAsync"/>
    /// itself only acts once a day. Skipped while <paramref name="videoRunning"/> says a video download is going.
    /// </summary>
    public void StartBackgroundUpdates(Func<bool> videoRunning, TimeSpan? firstCheck = null)
    {
        _videoRunning = videoRunning;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = firstCheck ?? TimeSpan.FromMinutes(2) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            TryUpdate();
        };
        _timer.Start();
    }

    public void StopBackgroundUpdates() => _timer?.Stop();

    /// <summary>Starts an update if one is due and nothing is downloading a video. Runs on the UI thread.</summary>
    public void TryUpdate()
    {
        if (!_update.IsCompleted || !tools.IsInstalled || _videoRunning?.Invoke() == true) return;
        _update = Task.Run(async () =>
        {
            try
            {
                var result = await tools.UpdateAsync();
                if (!result.Ran) return;
                log.LogInformation("yt-dlp update: ok={Ok} {Output}; Deno {Deno} updated={Updated} {Error}",
                    result.YtDlpOk, result.YtDlpOutput, result.DenoVersion, result.DenoUpdated, result.DenoError);
            }
            catch (Exception ex)
            {
                // Updating is a nicety; the installed tools keep working.
                log.LogWarning(ex, "Video tools update failed");
            }
        });
    }

    /// <summary>"ffmpeg: 64 of 192 MB" style text for the setup step.</summary>
    public static string Describe(ToolSetupProgress p) => p.Stage switch
    {
        ToolSetupStage.Downloading when p.TotalBytes is > 0 =>
            $"Downloading {p.Tool}: {Format.Bytes(p.BytesReceived)} of {Format.Bytes(p.TotalBytes)}",
        ToolSetupStage.Downloading => $"Downloading {p.Tool}: {Format.Bytes(p.BytesReceived)}",
        ToolSetupStage.Verifying => $"Checking {p.Tool}…",
        ToolSetupStage.Extracting => $"Unpacking {p.Tool}…",
        _ => $"{p.Tool} is ready",
    };
}
