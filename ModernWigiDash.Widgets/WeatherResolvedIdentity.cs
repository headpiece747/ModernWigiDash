namespace ModernWigiDash.Widgets;

/// <summary>
/// The widget's resolved-identity module: the dropdown candidates, the
/// resolved population, the header city name, and the pending label
/// write-back, plus the two invalidation rules that mirror the client's
/// InvalidateCoordinates / InvalidateLocation. The widget keeps only the gate
/// discipline (every mutation runs under its <c>_forecastGate</c>) and the
/// UI-thread flush of the write-back; the state transitions live here, where
/// the widget tests and this module's own tests pin them directly.
/// </summary>
internal sealed class WeatherResolvedIdentity
{
    public WeatherResolvedIdentity(string neutralLabel) => CityName = neutralLabel;

    /// <summary>The dropdown candidates last resolved (kept by a Location
    /// Match pick, cleared by every other location input).</summary>
    public IReadOnlyList<GeocodeCandidate> Candidates { get; private set; } = [];

    /// <summary>The resolved population (0 = the fetch reported none).</summary>
    public double Population { get; private set; }

    /// <summary>The resolved header city name (neutral until a resolution).</summary>
    public string CityName { get; private set; }

    /// <summary>The resolved label awaiting its UI-thread write-back (the
    /// fetch continuation only sets this; Render flushes it).</summary>
    public string? PendingWriteback { get; private set; }

    /// <summary>
    /// Applies one resolution result. Null keeps the previous value — the
    /// "response omitted this section — keep the previous value" rule shared
    /// with the snapshot apply. <paramref name="population"/> follows the
    /// client's no-data sentinel: 0 clears, a non-zero value replaces, null
    /// keeps.
    /// </summary>
    public void Apply(IReadOnlyList<GeocodeCandidate>? candidates = null, double? population = null, string? resolvedName = null)
    {
        if (candidates is not null) Candidates = candidates;
        if (population is double p) Population = p;
        if (resolvedName is not null) CityName = resolvedName;
    }

    public void DropPendingWriteback() => PendingWriteback = null;

    public void SetPendingWriteback(string value) => PendingWriteback = value;

    /// <summary>Returns and clears the pending write-back, so a re-entrant
    /// render can never double-write the label.</summary>
    public string? TakePendingWriteback()
    {
        string? pending = PendingWriteback;
        PendingWriteback = null;
        return pending;
    }

    /// <summary>
    /// The LocationMatch mirror of the client's InvalidateCoordinates: the
    /// resolved name and population drop with the old resolution, but the
    /// candidates stay — a Location Match pick resolves against the candidates
    /// it was offered from. Also drops a pending label write-back: an edit that
    /// lands after a completed fetch must not be overwritten by the old
    /// identity's label on the next render.
    /// </summary>
    public void InvalidateCoordinates()
    {
        PendingWriteback = null;
        CityName = "";
        Population = 0;
    }

    /// <summary>
    /// The other-location-input mirror of the client's InvalidateLocation: the
    /// whole resolved identity (candidates, name, population) is void until the
    /// next fetch resolves the new input, so the render-model cache key turns
    /// and the header drops the old city immediately. Also drops a pending
    /// write-back (same race the coordinates invalidation closes).
    /// </summary>
    public void InvalidateLocation()
    {
        PendingWriteback = null;
        Candidates = [];
        CityName = "";
        Population = 0;
    }

    /// <summary>
    /// The resolution inputs that force a re-fetch on change — an alias of
    /// <see cref="WeatherQueryKey.InvalidationProperties"/> (the owner of the
    /// set, ADR-0006: every key field except LocationMatch, which has its own
    /// branch in OnPropertyChanged). The drift test pins this set to the
    /// WeatherLocation record, so a new resolution input can never change
    /// the identity without a re-fetch.
    /// </summary>
    internal static readonly string[] ResolutionInvalidationProperties = WeatherQueryKey.InvalidationProperties;
}
