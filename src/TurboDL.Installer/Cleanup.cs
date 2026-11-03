namespace TurboDL.Installer;

/// <summary>Leftovers that aren't part of the install itself.</summary>
public static class Cleanup
{
    /// <summary>The exe names whose unpack folders are TurboDL's. .NET names the folder after the exe's file name.</summary>
    public static IReadOnlyList<string> SingleFileApps { get; } = ["TurboDL.App", "TurboDL.NativeHost", "TurboDL.Setup"];

    /// <summary>
    /// True for TurboDL's unpack folder names: the three exes, and the downloaded Setup under any name it was saved as
    /// (TurboDL-Setup-0.6.0, TurboDL-Setup-0.6.0 (1), ...).
    /// </summary>
    public static bool IsOurs(string appFolderName) =>
        SingleFileApps.Contains(appFolderName, StringComparer.OrdinalIgnoreCase)
        || appFolderName.StartsWith("TurboDL-Setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes &lt;extractDir&gt;\&lt;app&gt;\&lt;build id&gt; folders. Every TurboDL build unpacks its native libraries into a
    /// new one, and .NET never deletes them. A folder a running TurboDL loaded libraries from is kept (Windows would
    /// let it be renamed and its not-yet-loaded files deleted, which would break that copy later). A folder with
    /// files open in it can't be renamed, so it's kept too: renaming first means a folder is never half deleted.
    /// </summary>
    /// <param name="except">A folder to keep, e.g. the one this Setup runs from.</param>
    /// <param name="inUse">Whether a running process uses a folder. Default: <see cref="UsedByRunningProcess"/>.</param>
    public static (int Deleted, int Kept) ExtractFolders(string extractDir, string? except = null, Func<string, string, bool>? inUse = null)
    {
        inUse ??= UsedByRunningProcess;
        int deleted = 0, kept = 0;
        if (!Directory.Exists(extractDir)) return (0, 0);
        foreach (var parent in Directory.GetDirectories(extractDir))
        {
            var app = Path.GetFileName(parent);
            if (!IsOurs(app)) continue;
            foreach (var folder in Directory.GetDirectories(parent))
            {
                if (except is not null && string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(except).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)
                    || inUse(app, folder))
                {
                    kept++;
                    continue;
                }

                var doomed = $"{folder}.delete-{Environment.ProcessId}";
                try
                {
                    Directory.Move(folder, doomed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    kept++;
                    continue;
                }
                Directory.Delete(doomed, recursive: true);
                deleted++;
            }
            if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        return (deleted, kept);
    }

    /// <summary>
    /// True when a running &lt;app&gt;.exe has loaded a library from <paramref name="folder"/>, or when there's a running
    /// &lt;app&gt;.exe whose libraries can't be listed (another user's or an elevated one): then every folder counts as in use.
    /// </summary>
    public static bool UsedByRunningProcess(string app, string folder)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd('\\') + '\\';
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(app))
        {
            using (process)
            {
                try
                {
                    foreach (System.Diagnostics.ProcessModule module in process.Modules)
                    {
                        if (module.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    if (!process.HasExitedSafe()) return true;
                }
            }
        }
        return false;
    }

    private static bool HasExitedSafe(this System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>The yt-dlp/ffmpeg/Deno/7-Zip folder TurboDL downloads on first use (re-downloadable, not the user's data).</summary>
    public static string ToolsDir(InstallLocations locations) => Path.Combine(locations.UserDataDir, "tools");

    /// <summary>Deletes the downloaded tools, and with <paramref name="userData"/> the whole data folder (list, settings, logs).</summary>
    public static void UserData(InstallLocations locations, bool tools, bool userData)
    {
        if (userData)
        {
            if (Directory.Exists(locations.UserDataDir)) Directory.Delete(locations.UserDataDir, recursive: true);
        }
        else if (tools && Directory.Exists(ToolsDir(locations)))
        {
            Directory.Delete(ToolsDir(locations), recursive: true);
        }
    }
}
