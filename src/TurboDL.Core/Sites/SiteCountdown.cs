using System.Globalization;
using System.Text.RegularExpressions;

namespace TurboDL.Core.Sites;

/// <summary>Reads the number a countdown element shows, for a site config's countdown step.</summary>
public static partial class SiteCountdown
{
    /// <summary>The seconds shown: "12", "Wait 12 seconds", "01:30" (90). Null when the text has no number.</summary>
    public static int? SecondsLeft(string text)
    {
        if (Clock().Match(text) is { Success: true } clock)
            return int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                   + int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
        return Number().Match(text) is { Success: true } number ? int.Parse(number.Value, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"\b(\d{1,2}):([0-5]\d)\b")]
    private static partial Regex Clock();

    [GeneratedRegex(@"\d{1,5}")]
    private static partial Regex Number();
}
