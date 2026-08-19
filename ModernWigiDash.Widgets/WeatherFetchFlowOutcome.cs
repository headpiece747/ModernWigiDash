namespace ModernWigiDash.Widgets;

/// <summary>
/// The verdict of one run of the fetch flow (<see
/// cref="WeatherFetchFlow.RunFetchAsync"/> — the widget's
/// <c>FetchLiveWeatherAsync</c> forwards to it): what happened to the fetch's
/// outcome on its way through the flow's identity/apply gates. Lets tests
/// assert the flow deterministically at the module interface instead of
/// polling side effects — and documents the outcome vocabulary the fetch
/// path actually produces.
/// </summary>
internal enum WeatherFetchFlowOutcome
{
    /// <summary>A fresh snapshot passed every gate and was applied.</summary>
    Applied,

    /// <summary>A genuine same-name tie passed the identity/apply gates and
    /// was applied: the tied candidates populate the Location Match dropdown,
    /// the queried name becomes the header (the honest "this is what you
    /// asked for"), and the data state resets to its placeholder — no
    /// snapshot exists, and a previous city's scalars never render under a
    /// tie's header. The user escapes the tie through the pick path.</summary>
    AppliedTie,

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
