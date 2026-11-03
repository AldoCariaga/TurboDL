using System.Net;
using System.Net.Http.Headers;
using TurboDL.Core.Engine;

namespace TurboDL.Core.Video;

/// <summary>Resumable, checksum-verified downloads of tool files, shared by the video tools and 7-Zip setup.</summary>
internal static class ToolDownloads
{
    internal const int BufferSize = 1 << 16;

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="part"/> and checks it against <paramref name="expectedSha256"/>
    /// (lower-case hex). A mismatch after resuming gets one clean retry; otherwise the file is deleted and
    /// ChecksumMismatchException is thrown. Returns <paramref name="part"/>.
    /// </summary>
    internal static async Task<string> DownloadVerifiedAsync(HttpClient http, string tool, string url, string part,
        string expectedSha256, string assetName, TimeSpan stallTimeout, IProgress<ToolSetupProgress>? progress,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var resumed = await DownloadToPartAsync(http, tool, url, part, stallTimeout, progress, ct).ConfigureAwait(false);
            progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Verifying));
            var actual = await Checksums.Sha256OfFileAsync(part, ct).ConfigureAwait(false);
            if (actual == expectedSha256) return part;

            File.Delete(part);
            // A leftover .part may belong to an older build of the same file name; one clean retry settles it.
            if (!resumed || attempt > 0) throw new ChecksumMismatchException(assetName, expectedSha256, actual);
        }
    }

    /// <summary>Downloads to <paramref name="part"/>, resuming it. True when it carried on from an existing partial file.</summary>
    internal static async Task<bool> DownloadToPartAsync(HttpClient http, string tool, string url, string part,
        TimeSpan stallTimeout, IProgress<ToolSetupProgress>? progress, CancellationToken ct)
    {
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TurboHttp.ApplyHeaders(request, null);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (response.Content.Headers.ContentRange?.Length == existing) return true; // Already complete.
            // Longer than the file on the server: a leftover from something else. Start over.
            File.Delete(part);
            return await DownloadToPartAsync(http, tool, url, part, stallTimeout, progress, ct).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
            throw new ToolSetupException($"Downloading {Path.GetFileName(url)} failed: HTTP {(int)response.StatusCode}.");

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        long? total = response.Content.Headers.ContentRange?.Length
                      ?? (response.Content.Headers.ContentLength is { } len ? existing + len : null);

        await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, BufferSize, useAsync: true);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        var received = existing;
        var lastReport = DateTime.MinValue;
        progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Downloading, received, total));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(stallTimeout);
            int n;
            try
            {
                n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolSetupException(
                    $"Downloading {Path.GetFileName(url)} stalled (nothing for {stallTimeout.TotalSeconds:0} s). Try again to resume.");
            }
            if (n == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            received += n;
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Downloading, received, total));
            }
        }
        progress?.Report(new ToolSetupProgress(tool, ToolSetupStage.Downloading, received, total));
        if (total is { } t && received != t)
            throw new ToolSetupException($"Downloading {Path.GetFileName(url)} ended early ({received:N0} of {t:N0} bytes). Try again to resume.");
        return resumed;
    }
}
