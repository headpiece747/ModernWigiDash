namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather cluster's row-cap module: how many forecast rows the data
/// module keeps (the fetch-tier caps) and how many the renderer can draw
/// (the draw-tier caps). One owner for both tiers so the draw ≤ fetch
/// invariant is a single spelling, pinned by a test — a draw strip can never
/// request more rows than the data module provides.
/// </summary>
internal static class WeatherForecastLimits
{
    /// <summary>The maximum daily forecast rows the client ever keeps — the
    /// parse cap, the deserialized-cache cap, and the API's own response
    /// length share this one limit.</summary>
    public const int MaxFetchDays = 7;

    /// <summary>The maximum hourly forecast rows the client ever keeps — the
    /// parse cap, the deserialized-cache cap, and the API's own response
    /// length share this one limit.</summary>
    public const int MaxFetchHours = 12;

    /// <summary>The daily-strip draw cap — the number of day columns the
    /// renderer can draw. The display model caps at this; the renderer's
    /// re-caps reference the same constant, so a change edits one spelling.</summary>
    public const int MaxStripDays = 5;

    /// <summary>The hourly-strip draw cap — the number of hour columns the
    /// renderer can draw. The display model caps at this; the renderer's
    /// re-caps reference the same constant, so a change edits one spelling.</summary>
    public const int MaxStripHours = 6;
}
