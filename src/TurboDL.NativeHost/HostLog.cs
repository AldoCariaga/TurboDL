namespace TurboDL.NativeHost;

/// <summary>
/// The host's log. stdout belongs to Chrome's framing, so diagnostics go to a file (and stderr, which Chrome shows
/// when started with --enable-logging). Logging never throws.
/// </summary>
public sealed class HostLog(string? path, TextWriter? mirror = null)
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();

    public static HostLog None { get; } = new(null);

    /// <summary>%LOCALAPPDATA%\TurboDL\logs\nativehost.log, or TURBODL_HOST_LOG when set (tests, development).</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("TURBODL_HOST_LOG") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL", "logs",
                "nativehost.log");

    public string? FilePath { get; } = path;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        lock (_gate)
        {
            try { mirror?.WriteLine(line); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            if (FilePath is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another host instance may hold the file for a moment; a lost log line is fine.
            }
        }
    }
}
