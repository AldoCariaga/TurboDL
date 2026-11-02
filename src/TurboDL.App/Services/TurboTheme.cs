using System.Windows.Media;
using TurboDL.Core.Settings;
using ThemeMode = TurboDL.Core.Settings.ThemeMode;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TurboDL.App.Services;

/// <summary>
/// Light/dark follows Windows unless Settings picks one; the accent is always TurboDL's own deep teal.
/// WPF-UI uses the "secondary" accent for filled controls and the "tertiary" one for accent text,
/// so each theme gets a ramp built around its target fill colour.
/// </summary>
public static class TurboTheme
{
    // Light: #0E7C86 fill, darker teal for text on light surfaces.
    private static readonly Color[] Light = [Hex(0x14939E), Hex(0x11878F), Hex(0x0E7C86), Hex(0x0A6770)];

    // Dark: #4CC2C4 fill, lighter teal for text on dark surfaces.
    private static readonly Color[] Dark = [Hex(0x2FA6A9), Hex(0x3DB4B6), Hex(0x4CC2C4), Hex(0x73D3D4)];

    private static ThemeMode _mode = ThemeMode.System;

    public static void Initialize(ThemeMode mode)
    {
        ApplicationThemeManager.Changed += (theme, _) => ApplyAccent(theme);
        Set(mode);
    }

    public static void Set(ThemeMode mode)
    {
        _mode = mode;
        if (mode == ThemeMode.System) ApplicationThemeManager.ApplySystemTheme(false);
        else ApplicationThemeManager.Apply(mode == ThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.Mica, updateAccent: false);
        ApplyAccent(ApplicationThemeManager.GetAppTheme());
    }

    /// <summary>
    /// Called with every WM_SETTINGCHANGE the main window receives. Handled here rather than with WPF-UI's
    /// SystemThemeWatcher because that needs a loaded window, and TurboDL may start hidden in the tray.
    /// </summary>
    public static void OnSettingChange(string? area)
    {
        if (_mode == ThemeMode.System && area == "ImmersiveColorSet") Set(ThemeMode.System);
    }

    private static void ApplyAccent(ApplicationTheme theme)
    {
        var ramp = theme == ApplicationTheme.Dark ? Dark : Light;
        ApplicationAccentColorManager.Apply(ramp[0], ramp[1], ramp[2], ramp[3]);
    }

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
