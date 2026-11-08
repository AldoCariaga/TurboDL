using System.Diagnostics;
using System.Text;

namespace TurboDL.Core.Video;

/// <summary>How a run of an external tool ended.</summary>
public sealed record ToolRunResult(int ExitCode, bool TimedOut, IReadOnlyList<string> StdErr);

/// <summary>
/// Runs yt-dlp (or another console tool) with no window, UTF-8 I/O, and lines streamed to callbacks.
/// Cancelling or timing out kills the whole process tree, so ffmpeg children don't outlive it, and every run is in
/// <see cref="JobObject.Tools"/>, so nothing outlives TurboDL either.
/// </summary>
public static class ToolProcess
{
    /// <summary>Stderr lines kept for error messages (the tail of the output).</summary>
    public const int MaxStdErrLines = 200;

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <param name="splitStdOutOnProgress">
    /// Also end a stdout "line" at a backspace or carriage return. Tools like 7-Zip redraw a progress line in place with
    /// those and never end it, so with plain lines nothing would arrive until the tool finished.
    /// </param>
    public static async Task<ToolRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        TimeSpan? timeout,
        CancellationToken ct,
        bool splitStdOutOnProgress = false)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = psi };
        process.Start();
        // Into TurboDL's kill-on-close job, so the tool and everything it starts die with TurboDL even if it crashes
        // or is killed. Process.Start can't create it suspended, so there's a short window between start and here:
        // a child the tool starts in that window (yt-dlp takes far longer than that to reach ffmpeg) or a TurboDL
        // crash inside it would escape the job. If Windows refuses the job, the cancel/timeout tree kill below still applies.
        JobObject.Tools?.TryAdd(process);
        // yt-dlp never needs input; closing stdin means a prompt can't hang it.
        process.StandardInput.Close();

        var stderr = new Queue<string>();
        var outPump = splitStdOutOnProgress ? PumpProgressAsync(process.StandardOutput, onStdOut) : PumpAsync(process.StandardOutput, onStdOut);
        var errPump = PumpAsync(process.StandardError, line =>
        {
            lock (stderr)
            {
                stderr.Enqueue(line);
                if (stderr.Count > MaxStdErrLines) stderr.Dequeue();
            }
            onStdErr?.Invoke(line);
        });

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainAsync(outPump, errPump).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ToolRunResult(-1, true, Snapshot(stderr));
        }

        // WaitForExitAsync returns at process exit; the pipes may still hold the last lines.
        await DrainAsync(outPump, errPump).ConfigureAwait(false);
        return new ToolRunResult(process.ExitCode, false, Snapshot(stderr));
    }

    private static List<string> Snapshot(Queue<string> lines)
    {
        lock (lines) return [.. lines];
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting at the same moment; WaitForExit below still settles it.
        }
    }

    private static async Task DrainAsync(Task outPump, Task errPump)
    {
        // A grandchild that escaped the kill could keep a pipe open; don't wait on it forever.
        await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    private static async Task PumpAsync(StreamReader reader, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            onLine?.Invoke(line);
    }

    /// <summary>Like <see cref="PumpAsync"/>, but a backspace or carriage return ends a piece too. Blank pieces are skipped.</summary>
    private static async Task PumpProgressAsync(StreamReader reader, Action<string>? onPiece)
    {
        var buffer = new char[4096];
        var piece = new StringBuilder();
        void Flush()
        {
            if (piece.Length == 0) return;
            var text = piece.ToString();
            piece.Clear();
            if (!string.IsNullOrWhiteSpace(text)) onPiece?.Invoke(text);
        }

        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is '\n' or '\r' or '\b') Flush();
                else piece.Append(buffer[i]);
            }
        }
        Flush();
    }
}
