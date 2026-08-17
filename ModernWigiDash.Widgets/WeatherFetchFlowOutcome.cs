namespace ModernWigiDash.Widgets;

/// <summary>
/// The verdict of one <see cref="WeatherForecastWidget.FetchLiveWeatherAsync"/>
/// run: what happened to the fetch's outcome on its way through the widget's
/// identity/apply gates. Lets tests assert the flow deterministically instead
/// of polling side effects — and documents the outcome vocabulary the fetch
/// path actually produces.
/// </summary>
internal enum WeatherFetchFlowOutcome
{
    /// <summary>A fresh snapshot passed every gate and was applied.</summary>
    Applied,

    /// <summary>The result was stale, or the identity no longer matched at an
    /// await boundary, or the apply guard refused it — dropped (weather AND
    /// label), and a re-fetch of the current identity was requested.</summary>
    DroppedStale,

    /// <summary>Throttled / InFlight / Failed: no snapshot, previous state kept
    /// silently.</summary>
    Skipped,

    /// <summary>Teardown: the poll token was cancelled; nothing was applied or
    /// logged.</summary>
    Cancelled
}
