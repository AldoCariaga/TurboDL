using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurboDL.App.Services;

/// <summary>
/// File hosts' own icons (their favicons), shown beside "Solving" while a host's page is clicked through. The browser
/// takes one from the page the first time a host is solved (<see cref="BrowserCapture"/>); it's kept in
/// <see cref="Folder"/>, so later downloads from that host show it from the start. Keyed by the host's last two labels
/// ("dl3.host.com" → "host.com").
/// </summary>
public sealed class SiteIcons
{
    /// <summary>A bigger file isn't a site's icon.</summary>
    public const int MaxBytes = 256 * 1024;

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">Where the icons are kept; null is %LOCALAPPDATA%\TurboDL\icons.</param>
    public SiteIcons(string? folder) => Folder = folder ?? DefaultFolder;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL", "icons");

    public string Folder { get; }

    /// <summary>Raised (on any thread) when a host's icon is stored: its key, and the icon (frozen).</summary>
    public event Action<string, ImageSource>? Added;

    /// <summary>The host's last two labels, lower case; null for a link that isn't one.</summary>
    public static string? Key(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IdnHost.Length == 0) return null;
        return string.Join('.', uri.IdnHost.ToLowerInvariant().Split('.').TakeLast(2));
    }

    /// <summary>The icon of the link's host, or null when there's none yet.</summary>
    public ImageSource? Get(string url) =>
        Key(url) is { } key ? _cache.GetOrAdd(key, k => File.Exists(PathOf(k)) ? Decode(ReadOrNull(PathOf(k))) : null) : null;

    public bool Has(string url) => Get(url) is not null;

    /// <summary>Keeps <paramref name="bytes"/> as the host's icon when they're an image WPF can show; returns it, or null.</summary>
    public ImageSource? Store(string url, byte[] bytes)
    {
        if (Key(url) is not { } key || bytes.Length is 0 or > MaxBytes || Decode(bytes) is not { } icon) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(PathOf(key), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept on disk: it's still shown for this run.
        }
        _cache[key] = icon;
        Added?.Invoke(key, icon);
        return icon;
    }

    private string PathOf(string key) => Path.Combine(Folder, key + ".icon");

    private static byte[]? ReadOrNull(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A PNG, ICO, GIF, JPEG or BMP (whatever WPF reads), as the frame nearest 32 px; null for anything else (SVG).</summary>
    private static ImageSource? Decode(byte[]? bytes)
    {
        if (bytes is null or { Length: 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).ThenByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame is null || frame.PixelWidth < 8) return null;
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                       or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
