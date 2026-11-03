namespace TurboDL.Installer;

/// <summary>A file to install: its name in the install folder and where its bytes come from.</summary>
public sealed record PayloadFile(string Name, Func<Stream> Open);

/// <summary>Writes and deletes the files in the install folder.</summary>
public static class InstallFiles
{
    /// <summary>How long to keep trying a file that's in use (Chrome may be running the host for a moment).</summary>
    public static TimeSpan InUseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Writes each file next to its target first, then swaps it in, so an interrupted copy never leaves a half-written
    /// exe in place.
    /// </summary>
    public static void Write(string installDir, IEnumerable<PayloadFile> files)
    {
        Directory.CreateDirectory(installDir);
        foreach (var file in files)
        {
            var target = Path.Combine(installDir, file.Name);
            var temp = target + ".new";
            using (var source = file.Open())
            using (var output = File.Create(temp))
                source.CopyTo(output);
            Retry(target, () => File.Move(temp, target, overwrite: true));
        }
    }

    /// <summary>Deletes TurboDL's files, then the folder if nothing else is left in it. Returns whether the folder is gone.</summary>
    public static bool Delete(string installDir)
    {
        if (!Directory.Exists(installDir)) return true;
        foreach (var name in Product.InstalledFiles.Append(Product.LegacyHostSettings))
        {
            var path = Path.Combine(installDir, name);
            if (File.Exists(path)) Retry(path, () => File.Delete(path));
            if (File.Exists(path + ".new")) File.Delete(path + ".new");
        }
        if (Directory.EnumerateFileSystemEntries(installDir).Any()) return false;
        Directory.Delete(installDir);
        return true;
    }

    /// <summary>The installed files' total size, for Installed apps.</summary>
    public static long Size(string installDir) =>
        Product.InstalledFiles.Select(name => new FileInfo(Path.Combine(installDir, name))).Where(f => f.Exists).Sum(f => f.Length);

    private static void Retry(string path, Action action)
    {
        var deadline = DateTime.UtcNow + InUseTimeout;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"{Path.GetFileName(path)} in {Path.GetDirectoryName(path)} is in use, so Setup can't change it. " +
                    $"Close TurboDL, Chrome and Edge, then try again. ({ex.Message})", ex);
            }
        }
    }
}
