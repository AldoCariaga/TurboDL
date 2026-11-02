using System.IO;
using System.Runtime.InteropServices;

namespace TurboDL.App.Services;

public static partial class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's real Downloads folder, honouring a moved or redirected location.</summary>
    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, 0, out var path) == 0 && !string.IsNullOrEmpty(path))
                return path;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out string pszPath);
}
