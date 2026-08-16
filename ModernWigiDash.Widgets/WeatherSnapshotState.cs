namespace ModernWigiDash.Widgets;

/// <summary>
/// The widget's snapshot display state: the seven weather scalars, the two
/// forecast lists, and the two versions as one immutable record. The apply
/// policy merges a snapshot into it and returns a new value; the widget swaps
/// the result in under its gate, so a torn write can never be observed.
/// </summary>
internal sealed record WeatherSnapshotState
{
    public int DataVersion { get; init; }

    public double CurrentTempC { get; init; } = 25.0;

    public double FeelsLikeC { get; init; } = 22.2;

    public double Humidity { get; init; } = 87.0;

    public double WindSpeedKmH { get; init; } = 16.1;

    public double HighTempC { get; init; } = 26.6;

    public double LowTempC { get; init; } = 20.5;

    public int WeatherCode { get; init; } = 51;

    public int ForecastVersion { get; init; }

    public IReadOnlyList<DailyForecastItem> DailyForecasts { get; init; } = [];

    public IReadOnlyList<HourlyForecastItem> HourlyForecasts { get; init; } = [];
}
