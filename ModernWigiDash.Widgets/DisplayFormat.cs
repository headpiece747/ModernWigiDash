using System.Globalization;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The display-rules culture contract: one invariant, zero-aware number
/// formatter shared by every presentation module, so a comma-decimal machine
/// renders exactly what an en-US machine renders.
/// </summary>
public static class DisplayFormat
{
    /// <summary>Whole FPS with the unit suffix; non-positive and non-finite readings read "0 FPS".</summary>
    public static string Fps(double value)
        => double.IsFinite(value) && value > 0 ? value.ToString("F0", CultureInfo.InvariantCulture) + " FPS" : "0 FPS";

    /// <summary>Whole FPS without the unit suffix; non-positive and non-finite readings read "0".</summary>
    public static string FpsValue(double value)
        => double.IsFinite(value) && value > 0 ? value.ToString("F0", CultureInfo.InvariantCulture) : "0";

    /// <summary>One-decimal milliseconds; non-positive and non-finite readings read "0.0 ms".</summary>
    public static string Ms(double value)
        => double.IsFinite(value) && value > 0
            ? value.ToString("F1", CultureInfo.InvariantCulture) + " ms"
            : "0.0 ms";

    /// <summary>Whole percent with the suffix; non-positive and non-finite readings read "0%".</summary>
    public static string Pct(double value)
        => double.IsFinite(value) && value > 0 ? value.ToString("F0", CultureInfo.InvariantCulture) + "%" : "0%";

    /// <summary>Invariant whole number; negative values keep their sign,
    /// non-finite values read "0".</summary>
    public static string Count(double value)
        => double.IsFinite(value) ? value.ToString("F0", CultureInfo.InvariantCulture) : "0";

    /// <summary>Invariant value with the caller's requested format; negative values keep their sign.</summary>
    public static string Value(double value, string format)
        => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Group-separated invariant number rounded to exactly
    /// <paramref name="decimals"/> fraction digits — the caller's tier/choice
    /// is an upper bound, so a raw value with more digits never leaks them.</summary>
    public static string Number(decimal value, int decimals)
        => value.ToString("N" + decimals, CultureInfo.InvariantCulture);
}
