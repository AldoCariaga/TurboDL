using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace TurboDL.App.Services;

/// <summary>
/// Light/dark follows Windows; the accent is TurboDL's own deep teal rather than the system accent.
/// WPF-UI uses the "secondary" accent for filled controls and the "tertiary" one for accent text,
/// so each theme gets a ramp built around its target fill colour.
/// </summary>
public static class TurboTheme
{
    // Light: #0E7C86 fill, darker teal for text on light surfaces.
    private static readonly Color[] Light = [Hex(0x14939E), Hex(0x11878F), Hex(0x0E7C86), Hex(0x0A6770)];

    // Dark: #4CC2C4 fill, lighter teal for text on dark surfaces.
    private static readonly Color[] Dark = [Hex(0x2FA6A9), Hex(0x3DB4B6), Hex(0x4CC2C4), Hex(0x73D3D4)];

    public static void Initialize()
    {
        ApplicationThemeManager.ApplySystemTheme(false);
        ApplyAccent(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += (theme, _) => ApplyAccent(theme);
    }

    private static void ApplyAccent(ApplicationTheme theme)
    {
        var ramp = theme == ApplicationTheme.Dark ? Dark : Light;
        ApplicationAccentColorManager.Apply(ramp[0], ramp[1], ramp[2], ramp[3]);
    }

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
