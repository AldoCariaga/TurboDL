using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TurboDL.Core.Video;

namespace TurboDL.Core.Archives;

/// <summary>How an extraction ended. <see cref="Error"/> is one sentence for the user when <see cref="Ok"/> is false.</summary>
public sealed record ExtractResult(bool Ok, int ExitCode, string? Error, IReadOnlyList<string> Warnings);

/// <summary>How far 7-Zip has got with one archive (0–100), and the file it's writing when it says.</summary>
public sealed record ExtractProgress(int Percent, string? File);

/// <summary>
/// Extracts archives (RAR, 7z, zip, including multi-volume sets) by running 7z.exe through <see cref="ToolProcess"/>
/// with an argument list, never a shell. 7-Zip itself strips absolute paths and ".." from entry names, so every
/// extracted file stays inside the output folder; nothing extra is done here for that.
/// </summary>
public static partial class ArchiveExtractor
{
    /// <summary>
    /// A password 7-Zip is given so an encrypted archive fails at once ("Wrong password") instead of waiting for input.
    /// Unencrypted archives ignore it.
    /// </summary>
    public const string NoPassword = "turbodl-no-password";

    /// <summary>
    /// x -y -aou -bso0 -bsp1 -p&lt;NoPassword&gt; -o&lt;out&gt; -- &lt;firstVolume&gt;. -aou renames an extracted file whose name is
    /// already taken (several sets go into one folder, so one can't overwrite another's file). Normal output off;
    /// progress (-bsp1) on stdout, where <see cref="ExtractAsync"/> reads it.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string firstVolume, string outputDirectory) =>
        ["x", "-y", "-aou", "-bso0", "-bsp1", "-p" + NoPassword, "-o" + outputDirectory, "--", firstVolume];

    [GeneratedRegex(@"^\s*(?<p>\d{1,3})%(?:\s+\d+)?(?:\s+[-+U]\s+(?<f>.+?))?\s*$")]
    private static partial Regex ProgressPiece();

    /// <summary>
    /// Reads one piece of 7-Zip's progress line: "45%", " 45% 12", " 45% 12 - folder\file.bin". False for anything else
    /// (including the spaces 7-Zip writes to wipe the previous line).
    /// </summary>
    public static bool TryParseProgress(string piece, [NotNullWhen(true)] out ExtractProgress? progress)
    {
        var m = ProgressPiece().Match(piece);
        if (m.Success && int.TryParse(m.Groups["p"].Value, out var percent) && percent <= 100)
        {
            progress = new ExtractProgress(percent, m.Groups["f"].Success ? m.Groups["f"].Value : null);
            return true;
        }
        progress = null;
        return false;
    }

    /// <summary>
    /// Extracts a (multi-volume) archive with 7z.exe. 7-Zip finds the other volumes next to the first itself.
    /// Exit 0 = ok, 1 = ok with warnings, anything else = failed with a readable <see cref="ExtractResult.Error"/>.
    /// Cancelling kills 7-Zip (and the partial output stays for the caller to deal with).
    /// <paramref name="progress"/> hears each new percentage (from a background thread).
    /// </summary>
    public static async Task<ExtractResult> ExtractAsync(string sevenZipPath, string firstVolume, string outputDirectory,
        TimeSpan? timeout, CancellationToken ct, IProgress<ExtractProgress>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var last = -1;
        void OnPiece(string piece)
        {
            if (progress is null || !TryParseProgress(piece, out var p) || p.Percent == last) return;
            last = p.Percent;
            progress.Report(p);
        }
        var run = await ToolProcess.RunAsync(sevenZipPath, BuildArguments(firstVolume, outputDirectory), OnPiece, null, timeout, ct,
                splitStdOutOnProgress: true)
            .ConfigureAwait(false);

        if (run.TimedOut) return new ExtractResult(false, run.ExitCode, "7-Zip didn't finish in time.", []);
        return run.ExitCode switch
        {
            0 => new ExtractResult(true, 0, null, []),
            1 => new ExtractResult(true, 1, null,
                [.. run.StdErr.Where(l => l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))]),
            _ => new ExtractResult(false, run.ExitCode, DescribeError(run.ExitCode, run.StdErr), []),
        };
    }

    /// <summary>parent\baseName, or "baseName (2)", "(3)"… when that folder exists and isn't empty.</summary>
    public static string ChooseOutputDirectory(string parent, string baseName)
    {
        static bool Taken(string path) =>
            File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());

        var first = Path.Combine(parent, baseName);
        if (!Taken(first)) return first;
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(parent, $"{baseName} ({i})");
            if (!Taken(candidate)) return candidate;
        }
    }

    /// <summary>Turns 7-Zip's stderr into one sentence for the user.</summary>
    public static string DescribeError(int exitCode, IReadOnlyList<string> stderr)
    {
        bool Has(params string[] needles) =>
            stderr.Any(line => needles.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase)));

        if (Has("Wrong password", "encrypted"))
            return "The archive is password-protected. TurboDL can't extract it; open it with 7-Zip and enter the password.";
        if (Has("Missing volume", "Unexpected end of archive", "There are some data after the end"))
            return "A part is missing or incomplete. Check that every part downloaded fully.";
        if (Has("CRC Failed", "Data Error", "Headers Error"))
            return "A part is damaged (CRC error). Download the parts again.";
        if (Has("Can not open the file as archive", "Cannot open the file as archive"))
            return "This file isn't an archive 7-Zip can open.";
        if (exitCode == 8)
            return "7-Zip ran out of memory.";

        var last = stderr.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        return last is null ? $"7-Zip failed (exit code {exitCode})." : $"7-Zip failed (exit code {exitCode}): {last}";
    }
}
