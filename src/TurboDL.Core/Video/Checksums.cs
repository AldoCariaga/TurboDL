using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TurboDL.Core.Video;

public static partial class Checksums
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();

    /// <summary>
    /// Finds the SHA-256 for <paramref name="fileName"/> in a checksums file. Understands the
    /// <c>sha256sum</c> layout (<c>&lt;hash&gt;  &lt;name&gt;</c>, one per line, as yt-dlp and FFmpeg-Builds publish),
    /// and a file holding a single hash (Deno's PowerShell <c>Get-FileHash</c> output, a bare <c>.sha256</c>).
    /// Returns lower-case hex, or null when the file doesn't list it.
    /// </summary>
    public static string? FindSha256(string checksumsText, string fileName)
    {
        var allHashes = new List<string>();
        foreach (var raw in checksumsText.Split('\n'))
        {
            var tokens = raw.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            string? hash = null;
            var named = false;
            foreach (var token in tokens)
            {
                if (Sha256Hex().IsMatch(token)) hash = token.ToLowerInvariant();
                // sha256sum marks binary mode with a leading '*'; a path may also come before the name.
                else if (string.Equals(Path.GetFileName(token.TrimStart('*')), fileName, StringComparison.Ordinal)) named = true;
            }
            if (hash is null) continue;
            if (named) return hash;
            allHashes.Add(hash);
        }
        return allHashes.Count == 1 ? allHashes[0] : null;
    }

    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
