
namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure display rules for the clock: the AM/PM suffix, the date string, the
/// 12H/24H time format, and the analog hand angles.
/// </summary>
public static class ClockPresentation
{
    /// <summary>The AM/PM suffix for the 12H mode; empty for 24H.</summary>
    public static string AmPm(DateTime now, string timeFormat)
        => string.Equals(timeFormat, "24H", StringComparison.Ordinal) ? "" : now.ToString("tt", CultureInfo.InvariantCulture);

    /// <summary>The long date line under the time.</summary>
    public static string Date(DateTime now)
        => now.ToString("dddd, MMMM dd, yyyy", CultureInfo.InvariantCulture);

    /// <summary>Formats the digital clock time for the 12H/24H choice.</summary>
    public static string FormatClockTime(DateTime now, string timeFormat)
        => string.Equals(timeFormat, "24H", StringComparison.Ordinal) ? now.ToString("HH:mm", CultureInfo.InvariantCulture) : now.ToString("hh:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The analog hands' angles in radians (straight up = 0, clockwise). The
    /// hour hand sweeps with the minutes and the minute hand with the seconds,
    /// matching the widget's drawing convention.
    /// </summary>
    public static (float HourAngle, float MinuteAngle, float SecondAngle) HandAngles(DateTime now)
        => (
            (now.Hour % 12 + now.Minute / 60f) * 30f * (float)(Math.PI / 180f),
            (now.Minute + now.Second / 60f) * 6f * (float)(Math.PI / 180f),
            now.Second * 6f * (float)(Math.PI / 180f));
}
