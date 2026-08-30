namespace ModernWigiDash.Widgets;

/// <summary>
/// The widget's snapshot display state: the eight weather scalars (the
/// day/night flag included), the two forecast lists, and the two versions as
/// one immutable record. The apply
/// policy merges a snapshot into it and returns a new value; the widget swaps
/// the result in under its gate, so a torn write can never be observed.
/// </summary>
internal sealed record WeatherSnapshotState
{
    public int DataVersion { get; init; }

    /// <summary>Whether a real snapshot has been committed to this state.
    /// The placeholder defaults below are display seeds for the no-data
    /// view, never real readings: while this is false the pane draws its
    /// no-data view instead of the placeholder scalars (a tie reset and a
    /// fresh state both land here; the apply policy's merge is the one
    /// writer that flips it).</summary>
    public bool HasData { get; init; }

    public double CurrentTempC { get; init; } = 25.0;

    public double FeelsLikeC { get; init; } = 22.2;

    public double Humidity { get; init; } = 87.0;

    public double WindSpeedKmH { get; init; } = 16.1;

    public double HighTempC { get; init; } = 26.6;

    public double LowTempC { get; init; } = 20.5;

    public int WeatherCode { get; init; } = 51;

    /// <summary>The displayed day/night flag (the snapshot's fact, null-kept
    /// on apply; unknown — including every cached snapshot — reads as day).</summary>
    public bool IsDay { get; init; } = true;

    public int ForecastVersion { get; init; }

    public IReadOnlyList<DailyForecastItem> DailyForecasts { get; init; } = [];

    public IReadOnlyList<HourlyForecastItem> HourlyForecasts { get; init; } = [];
}
