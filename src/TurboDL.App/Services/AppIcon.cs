using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurboDL.App.Services;

/// <summary>
/// TurboDL's mark, drawn in code: a teal rounded square with a white arrow landing on a line.
/// Used for the window, the taskbar and the tray until the shipped .ico arrives.
/// </summary>
public static partial class AppIcon
{
    private static readonly Color Teal = Color.FromRgb(0x0E, 0x7C, 0x86);

    public static BitmapSource Render(int size)
    {
        var s = size / 32.0;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Teal), null, new Rect(1 * s, 1 * s, 30 * s, 30 * s), 7 * s, 7 * s);
            var pen = new Pen(Brushes.White, 3 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawLine(pen, new Point(16 * s, 7 * s), new Point(16 * s, 19 * s));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(10.5 * s, 14 * s), false, false);
                g.LineTo(new Point(16 * s, 19.5 * s), true, true);
                g.LineTo(new Point(21.5 * s, 14 * s), true, true);
            }
            dc.DrawGeometry(null, pen, head);
            dc.DrawLine(pen, new Point(9.5 * s, 24.5 * s), new Point(22.5 * s, 24.5 * s));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A Win32 icon handle for the tray. The caller owns it and must call <see cref="Destroy"/>.</summary>
    public static nint CreateHIcon(int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(size)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var png = ms.ToArray();
        return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
    }

    public static void Destroy(nint icon)
    {
        if (icon != 0) DestroyIcon(icon);
    }

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer, int cxDesired, int cyDesired, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);
}
