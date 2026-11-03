using System.Diagnostics;
using TurboDL.Core.Ipc;
using TurboDL.Installer;

namespace TurboDL.Setup;

/// <summary>A TurboDL running from the install folder holds its exe open, so it has to quit before files change.</summary>
public static class RunningApp
{
    /// <summary>TurboDL.App processes started from <paramref name="installDir"/> (the ones this user can see).</summary>
    public static List<Process> Find(string installDir)
    {
        var target = Path.GetFullPath(Path.Combine(installDir, Product.AppExe));
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.AppExe)))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            if (path is not null && string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase)) found.Add(process);
            else process.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Asks a TurboDL running from <paramref name="installDir"/> to quit ({"type":"quit"} on this user's pipe; it exits
    /// as tray > Exit does) and waits for it. True when none is left. Only one TurboDL runs per user, so when one runs
    /// from this folder, it's the one on the pipe.
    /// </summary>
    public static async Task<bool> QuitAsync(string installDir, TimeSpan timeout)
    {
        var running = Find(installDir);
        if (running.Count == 0) return true;
        try
        {
            try
            {
                await new IpcClient { ConnectTimeout = TimeSpan.FromSeconds(2), ReplyTimeout = TimeSpan.FromSeconds(5) }
                    .SendAsync(IpcRequest.Quit());
            }
            catch (Exception ex) when (ex is IpcUnavailableException or IOException or TimeoutException or IpcProtocolException
                                           or OperationCanceledException or System.Text.Json.JsonException)
            {
                // An older TurboDL without quit, or one that's stuck: the user is asked to close it.
            }

            var deadline = DateTime.UtcNow + timeout;
            foreach (var process in running)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !process.WaitForExit(left)) return false;
            }
            return true;
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }
    }
}
