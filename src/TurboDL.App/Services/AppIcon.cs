using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TurboDL.App.Services;

/// <summary>
/// TurboDL's icon: assets/TurboDL.ico (drawn by scripts/make-icons.ps1), embedded as a WPF resource.
/// The same file is the exe's ApplicationIcon.
/// </summary>
public static partial class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/Assets/TurboDL.ico");

    private static BitmapFrame? _window;
    private static byte[]? _file;

    /// <summary>For Window.Icon. It carries every size in the .ico, so WPF picks the small and large icon for the DPI.</summary>
    public static BitmapFrame Window
    {
        get
        {
            if (_window is null)
            {
                var frame = BitmapFrame.Create(Source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                _window = frame;
            }
            return _window;
        }
    }

    /// <summary>
    /// A Win32 icon handle <paramref name="size"/> pixels square, from the .ico's image of that size
    /// (or the next larger one, scaled down). The caller owns it and must call <see cref="Destroy"/>.
    /// </summary>
    public static nint CreateHIcon(int size)
    {
        var file = _file ??= ReadFile();
        var (offset, length) = PickImage(file, size);
        return CreateIconFromResourceEx(file.AsSpan(offset, length).ToArray(), (uint)length, true, 0x00030000, size, size, 0);
    }

    public static void Destroy(nint icon)
    {
        if (icon != 0) DestroyIcon(icon);
    }

    private static byte[] ReadFile()
    {
        using var stream = Application.GetResourceStream(Source).Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The image in an .ico file that best fits <paramref name="size"/>: an exact match, else the smallest larger one, else the largest.</summary>
    private static (int Offset, int Length) PickImage(ReadOnlySpan<byte> ico, int size)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        int best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = ico.Slice(6 + 16 * i, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            // While the best is too small, anything bigger is better; once it's big enough, only a closer fit that's still big enough.
            var better = best < 0 || (bestSize < size ? width > bestSize : width >= size && width < bestSize);
            if (better)
            {
                best = i;
                bestSize = width;
            }
        }
        if (best < 0) throw new InvalidDataException("The icon file has no images.");
        var chosen = ico.Slice(6 + 16 * best, 16);
        return ((int)BinaryPrimitives.ReadUInt32LittleEndian(chosen[12..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(chosen[8..]));
    }

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer, int cxDesired, int cyDesired, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);
}
