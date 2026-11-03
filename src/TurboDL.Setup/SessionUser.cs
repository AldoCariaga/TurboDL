using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TurboDL.Installer;

namespace TurboDL.Setup;

/// <summary>Checks that Setup runs as the person signed in, and cleans up after itself when it exits.</summary>
public static partial class SessionUser
{
    /// <summary>
    /// Null when Setup runs as the signed-in user. Otherwise a message: an elevated Setup started under another
    /// (admin) account would register TurboDL in that account's registry, not the user's.
    /// </summary>
    public static string? WrongAccount()
    {
        if (!Worker.IsElevated()) return null;
        var signedIn = SignedInUser();
        using var identity = WindowsIdentity.GetCurrent();
        if (signedIn is null || string.Equals(signedIn, identity.Name, StringComparison.OrdinalIgnoreCase)) return null;
        return $"Setup is running as {identity.Name}, but {signedIn} is signed in. TurboDL would be set up for the wrong account. " +
               "Start Setup normally (not with \"Run as administrator\" or as another user); it asks for administrator rights itself.";
    }

    /// <summary>DOMAIN\user of this Windows session, or null when it can't be read.</summary>
    private static string? SignedInUser()
    {
        string? Query(int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, infoClass, out var buffer, out _)) return null;
            try { return Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }

        var user = Query(WTSUserName);
        var domain = Query(WTSDomainName);
        if (string.IsNullOrEmpty(user)) return null;
        return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
    }

    /// <summary>
    /// The folder .NET unpacked this Setup's native libraries into (it's in use while Setup runs), or null.
    /// </summary>
    public static string? OwnExtractFolder(string extractDir)
    {
        var root = Path.GetFullPath(extractDir).TrimEnd('\\') + '\\';
        using var self = Process.GetCurrentProcess();
        foreach (ProcessModule module in self.Modules)
        {
            if (!module.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            // <extractDir>\<app>\<build id>\file.dll
            var relative = module.FileName[root.Length..].Split('\\');
            if (relative.Length >= 3) return Path.Combine(root, relative[0], relative[1]);
        }
        return null;
    }

    /// <summary>
    /// After Setup exits: deletes its unpack folder and, for an uninstall running from its temp copy, that copy.
    /// Nothing can delete a running exe's own files, so a hidden cmd waits a moment and does it.
    /// </summary>
    public static void CleanUpAfterExit(IEnumerable<string?> folders)
    {
        var commands = new List<string> { "ping -n 3 127.0.0.1 >nul" };
        foreach (var folder in folders.OfType<string>().Where(Directory.Exists))
        {
            commands.Add($"rd /s /q \"{folder}\"");
            // The <app> folder above a build folder, when that was the last one.
            if (Path.GetDirectoryName(folder) is { } parent && Cleanup.IsOurs(Path.GetFileName(parent)))
                commands.Add($"rd \"{parent}\" 2>nul");
        }
        if (commands.Count == 1) return;

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"{string.Join(" & ", commands)}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(start)?.Dispose();
    }

    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
