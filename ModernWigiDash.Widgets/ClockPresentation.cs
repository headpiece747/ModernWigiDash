namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure display rules for the digital clock: the AM/PM suffix and the date
/// string, previously formatted inline in the render path.
/// </summary>
public static class ClockPresentation
{
    /// <summary>The AM/PM suffix for the 12H mode; empty for 24H.</summary>
    public static string AmPm(DateTime now, string timeFormat)
        => timeFormat == "24H" ? "" : now.ToString("tt");

    /// <summary>The long date line under the time.</summary>
    public static string Date(DateTime now)
        => now.ToString("dddd, MMMM dd, yyyy");
}
