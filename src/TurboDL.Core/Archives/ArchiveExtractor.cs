using TurboDL.Core.Video;

namespace TurboDL.Core.Archives;

/// <summary>How an extraction ended. <see cref="Error"/> is one sentence for the user when <see cref="Ok"/> is false.</summary>
public sealed record ExtractResult(bool Ok, int ExitCode, string? Error, IReadOnlyList<string> Warnings);

/// <summary>
/// Extracts archives (RAR, 7z, zip, including multi-volume sets) by running 7z.exe through <see cref="ToolProcess"/>
/// with an argument list, never a shell. 7-Zip itself strips absolute paths and ".." from entry names, so every
/// extracted file stays inside the output folder; nothing extra is done here for that.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>
    /// A password 7-Zip is given so an encrypted archive fails at once ("Wrong password") instead of waiting for input.
    /// Unencrypted archives ignore it.
    /// </summary>
    public const string NoPassword = "turbodl-no-password";

    /// <summary>x -y -bso0 -bsp0 -p&lt;NoPassword&gt; -o&lt;out&gt; -- &lt;firstVolume&gt;</summary>
    public static IReadOnlyList<string> BuildArguments(string firstVolume, string outputDirectory) =>
        ["x", "-y", "-bso0", "-bsp0", "-p" + NoPassword, "-o" + outputDirectory, "--", firstVolume];

    /// <summary>
    /// Extracts a (multi-volume) archive with 7z.exe. 7-Zip finds the other volumes next to the first itself.
    /// Exit 0 = ok, 1 = ok with warnings, anything else = failed with a readable <see cref="ExtractResult.Error"/>.
    /// Cancelling kills 7-Zip (and the partial output stays for the caller to deal with).
    /// </summary>
    public static async Task<ExtractResult> ExtractAsync(string sevenZipPath, string firstVolume, string outputDirectory,
        TimeSpan? timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        var run = await ToolProcess.RunAsync(sevenZipPath, BuildArguments(firstVolume, outputDirectory), null, null, timeout, ct)
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
