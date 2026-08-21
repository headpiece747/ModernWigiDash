namespace ModernWigiDash.Widgets;

/// <summary>
/// The client-side fetch-control state machine: the 5-minute throttle window,
/// the single-flight claim, and the resolved-identity stamp — the mutable twin
/// of the widget's pure <see cref="WeatherResolvedIdentity"/>. The resolved
/// identity itself is the shared <see cref="WeatherResolutionState"/> value
/// (candidates, name, population) — the widget's twin holds the same type,
/// and both route their drops through
/// <see cref="WeatherInvalidation.Drop"/>. Every state transition that carries
/// a rule (compare + stamp under one gate, the advance-clears-old-coordinates
/// rule, invalidation) is an atomic operation here; the client keeps only the
/// orchestration. One gate owns the resolved identity fields (query,
/// coordinates, the shared identity value) with the throttle, so no caller
/// can tear the compare from the stamp or leave old coordinates under a new
/// query.
/// </summary>
internal sealed class WeatherFetchControl
{
    /// <summary>The fetch cool-down window — the one cadence constant the
    /// widget's refresh loop and every throttle check share (a change edits
    /// one value).</summary>
    internal static readonly TimeSpan FetchWindow = TimeSpan.FromMinutes(5);

    /// <summary>Test seam: injectable clock for throttling.</summary>
    internal TimeProvider Clock { get; set; }

    private readonly Lock _gate = new();
    private DateTime _lastFetchTime = DateTime.MinValue;
    private int _claim; // 1 = a fetch is in flight
    private string _lastLocationQuery = "";
    private double? _lat;
    private double? _lon;
    // The shared resolved-identity value — the ONE storage the client twin
    // keeps for the candidates/name/population. The widget's resolved-identity
    // twin holds the same value type, and both route their drops through
    // WeatherInvalidation.Drop, so the two twins can never drift.
    private WeatherResolutionState _resolution = WeatherResolutionState.Empty;

    internal WeatherFetchControl(TimeProvider clock) => Clock = clock;

    /// <summary>Test seams: the current state, read without the gate (the
    /// rules are exercised through the atomic operations; these exist so
    /// assertions can observe the state after a transition).</summary>
    internal DateTime LastFetchTimeUtc => _lastFetchTime;

    /// <summary>Whether the throttle has ever been stamped — the one client
    /// fact the cadence gate needs, as a named predicate (callers never
    /// compare the raw timestamp against <see cref="DateTime.MinValue"/>).</summary>
    internal bool HasFetched => _lastFetchTime != DateTime.MinValue;
    internal bool IsClaimHeld => _claim != 0;
    internal string LastLocationQuery => _lastLocationQuery;

    /// <summary>The shared resolved-identity value (the same type the
    /// widget's twin holds) — both twins route their drops through
    /// <see cref="WeatherInvalidation.Drop"/>.</summary>
    internal WeatherResolutionState ResolutionState => _resolution;
    internal IReadOnlyList<GeocodeCandidate> Candidates => _resolution.Candidates;
    internal double ResolvedPopulation => _resolution.Population;
    internal double? Lat => _lat;
    internal double? Lon => _lon;
    internal string ResolvedCityName => _resolution.ResolvedName;

    /// <summary>Sync throttle pre-check for the render tick: true when the
    /// throttle window has elapsed since the last attempt. The first attempt
    /// (never-fetched) reads as elapsed; a failed attempt stamps the time, so
    /// failures cool down like successes. Read without the gate — a torn read
    /// is tolerable because <see cref="Begin"/>'s atomic claim is the
    /// authority.</summary>
    internal bool IsWindowElapsed()
        => Clock.GetUtcNow().UtcDateTime - _lastFetchTime >= FetchWindow;

    /// <summary>
    /// The atomic claim + throttle gate: acquires the single-flight claim,
    /// then applies the throttle window unless forced. <see cref="BeginResult.InFlight"/>
    /// leaves the OTHER claim held (the caller does nothing); <see cref="BeginResult.Throttled"/>
    /// releases our claim before returning — the caller's finally must release
    /// only for <see cref="BeginResult.Started"/>.
    /// </summary>
    internal BeginResult Begin(bool force)
    {
        if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0) return BeginResult.InFlight;
        if (!force && (Clock.GetUtcNow().UtcDateTime - _lastFetchTime) < FetchWindow)
        {
            Interlocked.Exchange(ref _claim, 0);
            return BeginResult.Throttled;
        }
        return BeginResult.Started;
    }

    /// <summary>Releases the single-flight claim (the fetch's finally).</summary>
    internal void End() => Interlocked.Exchange(ref _claim, 0);

    /// <summary>
    /// The single spelling of "the identity still matches the fetch's key":
    /// compares under the gate and, when it matches, stamps the throttle (an
    /// attempt cools down like a success). Returns whether the stamp was
    /// written — false means the identity changed mid-flight and the NEW
    /// identity's fetch must not be cooled down. Used by the failure path and
    /// the geocode leg; the success path uses <see cref="ConfirmAndStamp"/>,
    /// which also carries the resolved payload out under the same lock.
    /// </summary>
    internal bool Stamp(string queryKey)
    {
        lock (_gate)
        {
            if (!WeatherQueryKey.SameKey(_lastLocationQuery, queryKey)) return false;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    /// <summary>
    /// The success-path compare + stamp: confirms the identity still matches,
    /// stamps the throttle, and captures the resolved-identity payload
    /// (candidates, population) under the one gate — no invalidation can
    /// interleave and leave a stamp or payload for the OLD identity. Returns
    /// false (no stamp) when the identity changed mid-flight: the caller must
    /// report Stale, never apply or cache the snapshot.
    /// </summary>
    internal bool ConfirmAndStamp(string queryKey, out IReadOnlyList<GeocodeCandidate> candidates, out double population)
    {
        lock (_gate)
        {
            if (!WeatherQueryKey.SameKey(_lastLocationQuery, queryKey))
            {
                candidates = [];
                population = 0;
                return false;
            }
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            candidates = _resolution.Candidates;
            population = _resolution.Population;
            return true;
        }
    }

    /// <summary>Compares the identity key under the gate (the no-coordinates
    /// Stale check and the fetch's re-resolve condition).</summary>
    internal bool MatchesCurrent(string queryKey)
    {
        lock (_gate) { return WeatherQueryKey.SameKey(_lastLocationQuery, queryKey); }
    }

    /// <summary>
    /// Advances the resolution identity BEFORE the outcome is known. If the
    /// key changed (a silent reassignment — hydration, or a direct property
    /// write that bypassed invalidation — raced a previous resolution), the
    /// OLD identity's coordinates/name/population are cleared: a failed
    /// geocode for the new identity must not fall through with the previous
    /// place's state still set, and the completion check (which compares
    /// against THIS new key) would otherwise pass — fetching and caching the
    /// wrong city under the new identity. The geocode candidates SURVIVE the
    /// key change — they are cleared explicitly by the edit path's
    /// invalidation (InvalidateLocation / ClearCandidates), because the
    /// LocationMatch edit's own drop resets the query to empty while KEEPING
    /// the candidates the pick resolves against: the pick's fetch then
    /// advances from empty and must still find its row (the geocoder's
    /// zero-HTTP fast path). The population reset rides the same lock so the
    /// fetch's next read is one consistent view.
    /// </summary>
    internal void AdvanceResolution(string queryKey)
    {
        lock (_gate)
        {
            bool identityChanged = !WeatherQueryKey.SameKey(_lastLocationQuery, queryKey);
            _lastLocationQuery = queryKey;
            if (identityChanged)
            {
                _lat = null;
                _lon = null;
                _resolution = _resolution.With(resolvedName: "", population: 0);
            }
            else
            {
                _resolution = _resolution.With(population: 0);
            }
        }
    }

    /// <summary>Refreshes the "Location Match" dropdown's candidate list (a
    /// geocode that produced candidates; one that produced none leaves the
    /// last list untouched).</summary>
    internal void SetCandidates(IReadOnlyList<GeocodeCandidate> candidates)
    {
        lock (_gate) { _resolution = _resolution.With(candidates: candidates); }
    }

    /// <summary>Applies a winning resolution: the exact coordinates, the
    /// composed label, and (for a name/pick resolution) the population.</summary>
    internal void SetResolved(double lat, double lon, string name, double population)
    {
        lock (_gate)
        {
            _lat = lat;
            _lon = lon;
            _resolution = _resolution.With(resolvedName: name, population: population);
        }
    }

    /// <summary>Clears the coordinates and resolved name for an ambiguous tie —
    /// coordinates must never be guessed, and a previous resolution's name must
    /// not trap the next editor with a place the fetch never reached.</summary>
    internal void ClearCoordinates()
    {
        lock (_gate)
        {
            _lat = null;
            _lon = null;
            _resolution = _resolution.With(resolvedName: "");
        }
    }

    /// <summary>
    /// Applies a cache payload's identity under the gate: a non-empty current
    /// query that differs from the payload's key means a different identity's
    /// resolution has started — the payload must not be applied (returns
    /// false). An empty current query is the boot case, where the load is
    /// legitimate. On apply, the resolved name comes from the payload's
    /// carried name, else the cached coordinates formatted, else the neutral
    /// label — never an invented city. The throttle is primed so a freshly
    /// cached widget does not immediately re-fetch.
    /// </summary>
    internal bool TryApplyCacheIdentity(string queryKey, double? lat, double? lon, string? cachedName, out string appliedName)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_lastLocationQuery)
                && !WeatherQueryKey.SameKey(_lastLocationQuery, queryKey))
            {
                appliedName = "";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(cachedName))
            {
                appliedName = cachedName;
            }
            else if (lat is double cachedLat && lon is double cachedLon)
            {
                appliedName = WeatherLocationResolver.FormatCoordinates(cachedLat, cachedLon);
            }
            else
            {
                appliedName = WeatherPresentation.UnknownLocationLabel;
            }
            _resolution = _resolution.With(resolvedName: appliedName);
            _lat = lat;
            _lon = lon;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    /// <summary>
    /// Resets the resolved coordinates, name, population, query, and throttle
    /// so the next fetch re-resolves and runs immediately (an edit-flow
    /// location change, or a discarded cache load's rollback). The resolved
    /// population drops with the resolution result (this drop voids the old
    /// winner — the widget twin's <c>InvalidateCoordinates</c> makes the same
    /// clear); it rides back only with the new winner via <see
    /// cref="SetResolved"/>. Contrast <see cref="ClearCoordinates"/>, which
    /// SERVES an ambiguous tie where the old resolution stays the current
    /// best and keeps its population. The geocode candidates survive — the
    /// "Location Match" pick can still resolve against the candidates it was
    /// offered from; <see cref="ClearCandidates"/> drops them for the full
    /// invalidation.
    /// </summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _lat = null;
            _lon = null;
            _resolution = WeatherInvalidation.Drop(WeatherInvalidationKind.Coordinates, _resolution);
            _lastFetchTime = DateTime.MinValue;
            _lastLocationQuery = "";
        }
    }

    /// <summary>Drops the geocode candidates and population: a pick made
    /// against a previous location must never resolve against a changed
    /// Location/CountryCode/coords.</summary>
    internal void ClearCandidates()
    {
        lock (_gate)
        {
            _resolution = _resolution.With(population: 0, candidates: []);
        }
    }
}

/// <summary>The outcome of <see cref="WeatherFetchControl.Begin"/>.</summary>
internal enum BeginResult
{
    /// <summary>The claim was acquired; the caller runs the fetch and must
    /// call <see cref="WeatherFetchControl.End"/> in a finally.</summary>
    Started,

    /// <summary>Another fetch is already in flight — nothing to do.</summary>
    InFlight,

    /// <summary>The throttle window has not elapsed; the attempt cools down
    /// like a success.</summary>
    Throttled,
}
