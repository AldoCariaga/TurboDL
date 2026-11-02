namespace TurboDL.App.Services;

/// <summary>Human-readable sizes, speeds and durations, Explorer style (1 KB = 1024 bytes).</summary>
public static class Format
{
    public const string None = "—";

    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Bytes(long? bytes)
    {
        if (bytes is not long b) return None;
        if (b < 1024) return $"{b} bytes";
        double v = b;
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        // Three significant digits: 5.82 GB, 58.2 GB, 582 GB.
        var text = v < 10 ? v.ToString("0.##") : v < 100 ? v.ToString("0.#") : v.ToString("0");
        return $"{text} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond < 1 ? None : $"{Bytes((long)bytesPerSecond)}/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not TimeSpan t || t < TimeSpan.Zero) return None;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
