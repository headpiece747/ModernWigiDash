namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather cluster's ONE resolved-identity owner: the shared identity
/// value (<see cref="WeatherResolutionState"/> — the dropdown candidates, the
/// header city name, the population) plus the fetch-side fields (the throttle
/// stamp, the single-flight claim, the identity query, the coordinates) and
/// the display-side field (the pending label write-back), all under one gate.
/// <para>
/// The client and the display state hold a REFERENCE to this module instead
/// of each holding a copy of the identity, so the "the two sides never drift"
/// invariant is by construction, not a test pin: there is one storage, one
/// drop application site (<see cref="Invalidate"/> routes through
/// <see cref="WeatherInvalidation.Drop"/>), and no twin-equivalence ceremony.
/// </para>
/// <para>
/// Every transition that carries a rule (compare + stamp under one gate, the
/// advance-clears-old-coordinates rule, the cache-identity apply, the
/// write-back queue/take serialization, invalidation) is an atomic operation
/// here; the client keeps only the orchestration, and the display state keeps
/// only the snapshot state and the render copies.
/// </para>
/// </summary>
internal sealed class WeatherResolution(TimeProvider clock, string neutralLocationLabel)
{
    /// <summary>The fetch cool-down window — the one cadence constant the
    /// widget's refresh loop and every throttle check share (a change edits
    /// one value).</summary>
    internal static readonly TimeSpan FetchWindow = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private DateTime _lastFetchTime = DateTime.MinValue;
    private int _claim; // 1 = a fetch is in flight
    private string _lastLocationQuery = "";
    private double? _lat;
    private double? _lon;
    private WeatherResolutionState _identity = new(neutralLocationLabel, 0, []);
    private string? _pendingWriteback;

    /// <summary>Test seam: injectable clock for throttling (read at stamp
    /// time, so a swap is observed by the next transition).</summary>
    internal TimeProvider Clock { get; set; } = clock;

    /// <summary>Test seams: the current state, read without the gate (the
    /// rules are exercised through the atomic operations; these exist so
    /// assertions can observe the state after a transition).</summary>
    internal DateTime LastFetchTimeUtc => _lastFetchTime;

    /// <summary>Whether the throttle has ever been stamped — the one client
    /// fact the cadence gate needs, as a named predicate (callers never
    /// compare the raw timestamp against <see cref="DateTime.MinValue"/>).</summary>
    internal bool HasFetched => _lastFetchTime != DateTime.MinValue;
    internal bool IsClaimHeld => _claim != 0;

    /// <summary>The current identity query key (the client-side live key of
    /// the <see cref="CaptureWindowGuard"/>: the client's guard re-checks the
    /// fetch's start key against this value). Read without the gate — a
    /// string is an immutable reference, so the read cannot tear; the verdict
    /// is a pure function of the value observed. Test seams also read it to
    /// observe the state after a transition.</summary>
    internal string LastLocationQuery => _lastLocationQuery;

    /// <summary>The shared resolved-identity value — the ONE storage both the
    /// client and the display state read; its transitions run only through
    /// this module's gated members.</summary>
    internal WeatherResolutionState Identity
    {
        get { lock (_gate) { return _identity; } }
    }
    internal IReadOnlyList<GeocodeCandidate> Candidates => _identity.Candidates;
    internal double ResolvedPopulation => _identity.Population;
    internal double? Lat => _lat;
    internal double? Lon => _lon;
    internal string ResolvedCityName => _identity.ResolvedName;

    /// <summary>The pending resolved-label write-back awaiting the
    /// UI-thread flush — read under the gate (the queue and the take run
    /// under it, so a read in between is consistent).</summary>
    internal string? PendingLabelWriteback
    {
        get { lock (_gate) { return _pendingWriteback; } }
    }

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
    /// identity's fetch must not be cooled down. The compare-and-stamp must
    /// be one gate section (an invalidation interleaved between a plain
    /// re-check and the stamp would write the OLD identity's throttle), so
    /// the transition keeps the ADR-0006 predicate under its own gate
    /// instead of routing through the capture window's plain re-check.
    /// Used by the failure path and the geocode leg; the success path uses
    /// <see cref="ConfirmAndStamp"/>, which also carries the resolved payload
    /// out under the same lock.
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
            candidates = _identity.Candidates;
            population = _identity.Population;
            return true;
        }
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
    /// invalidation (the Location kind's whole-identity drop), because the
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
                _identity = _identity.With(resolvedName: "", population: 0);
            }
            else
            {
                _identity = _identity.With(population: 0);
            }
        }
    }

    /// <summary>Refreshes the "Location Match" dropdown's candidate list (a
    /// geocode that produced candidates; one that produced none leaves the
    /// last list untouched).</summary>
    internal void SetCandidates(IReadOnlyList<GeocodeCandidate> candidates)
    {
        lock (_gate) { _identity = _identity.With(candidates: candidates); }
    }

    /// <summary>Applies a winning resolution: the exact coordinates, the
    /// composed label, and (for a name/pick resolution) the population.</summary>
    internal void SetResolved(double lat, double lon, string name, double population)
    {
        lock (_gate)
        {
            _lat = lat;
            _lon = lon;
            _identity = _identity.With(resolvedName: name, population: population);
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
            _identity = _identity.With(resolvedName: "");
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
            _identity = _identity.With(resolvedName: appliedName);
            _lat = lat;
            _lon = lon;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    /// <summary>
    /// The single edit-path invalidation, per drop kind (the boot-load's
    /// discarded-load rollback rides the Coordinates kind too): the resolved
    /// coordinates clear, the identity query + throttle reset (the next fetch
    /// re-resolves and runs immediately), the pending label write-back drops
    /// (an edit landing after a completed fetch must not be overwritten by
    /// the old identity's label on the next render), and the shared identity
    /// value drops through the single rule (<see cref="WeatherInvalidation.Drop"/>)
    /// — Coordinates keeps the offered candidates (a pick resolves against
    /// them), Location voids the whole identity into the one empty state.
    /// One module, one gate, one application site: the former twin
    /// equivalence is unrepresentable-broken. Contrast
    /// <see cref="ClearCoordinates"/>, which serves an ambiguous tie where
    /// the old resolution stays the current best and keeps its population.
    /// </summary>
    internal void Invalidate(WeatherInvalidationKind kind)
    {
        lock (_gate)
        {
            _lat = null;
            _lon = null;
            _pendingWriteback = null;
            _identity = WeatherInvalidation.Drop(kind, _identity);
            _lastFetchTime = DateTime.MinValue;
            _lastLocationQuery = "";
        }
    }

    /// <summary>
    /// The boot-load's discarded-load rollback: undoes the client-side
    /// commitment a cache load made (the throttle stamp and the identity
    /// query) AND restores the shared resolved-identity value to what it was
    /// BEFORE the load — the widget's header name survives a rejected load
    /// ("keeps the previous resolution"), while the next fetch still
    /// re-resolves and runs immediately (the throttle is back to never-fetched).
    /// This is distinct from <see cref="Invalidate"/>: that entry serves an
    /// EDIT (a new place was chosen, so the old identity's name must go),
    /// whereas here the identity never changed — only the load's own
    /// commitment must be withdrawn. The coordinates are restored from the
    /// committed payload (the load's applied lat/lon), so the follow-up fetch
    /// starts from the same coordinates it would have had without the load.
    /// </summary>
    internal void RollbackCacheLoad(double? lat, double? lon, WeatherResolutionState preLoadIdentity)
    {
        lock (_gate)
        {
            _lat = lat;
            _lon = lon;
            _identity = preLoadIdentity;
            _lastFetchTime = DateTime.MinValue;
            _lastLocationQuery = "";
        }
    }

    /// <summary>
    /// The one spelling of "the resolved label may still be written into
    /// Location": the name is non-empty, no CustomLabel claims the title (a
    /// label is display-only — writing the resolved name into Location would
    /// destroy the query), and the name is not already the Location (a
    /// no-op write would only churn a persistence + property event). The
    /// flow's queue and this take evaluate the SAME policy, and the take
    /// evaluates it under the gate — so an edit (a CustomLabel or a Location
    /// change) landing between the queue and the flush takes the same gate
    /// and is seen at the take, never sailed through an ungated flush check.
    /// </summary>
    internal static bool WritebackEligible(string? name, WeatherLocation currentLocation)
        => !string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(currentLocation.CustomLabel)
            && !string.Equals(name, currentLocation.Location, StringComparison.Ordinal);

    /// <summary>
    /// Queues a resolved-label write-back for the UI thread, only when the
    /// identity guard still passes — the check + set under the gate is one
    /// critical section (the edit-side clears and the UI-thread take take the
    /// same gate, so an edit either erases the queued value or is seen by the
    /// guard, and the take can never drop a concurrent queue). The queue
    /// carries only the name — the write-back eligibility decision is
    /// <see cref="WritebackEligible"/>, re-evaluated under the gate at take.
    /// </summary>
    internal void QueueLabelWriteback(Func<bool> identityGuard, string value)
    {
        lock (_gate)
        {
            if (identityGuard())
            {
                _pendingWriteback = value;
            }
        }
    }

    /// <summary>
    /// Returns and clears the pending write-back (the UI-thread flush) —
    /// under the gate, so a queue landing between the read and the clear can
    /// never be lost: the queue and the take serialize on the same lock. The
    /// take also decides whether the write may happen at all
    /// (<see cref="WritebackEligible"/> + the host's suppression flag): a
    /// vetoed take refuses AND KEEPS the value queued (a veto is a "not
    /// yet", never a "never" — a no-op write or a CustomLabel set between
    /// the queue and the flush must not silently lose the resolved label),
    /// so the next frame re-decides against the current host facts.
    /// </summary>
    internal string? TakePendingWriteback(WeatherLocation currentLocation, Func<bool> suppressed)
    {
        lock (_gate)
        {
            if (suppressed()) return null;
            if (!WritebackEligible(_pendingWriteback, currentLocation)) return null;
            string? pending = _pendingWriteback;
            _pendingWriteback = null;
            return pending;
        }
    }

    /// <summary>
    /// The display-state apply seam's identity half: the null-keeps
    /// replacement (the "response omitted this section — keep the previous
    /// value" rule shared with the snapshot merge; a provided population of
    /// 0 is the client's no-data sentinel: it clears, it does not keep) runs
    /// under the gate, so the identity copies land atomically with whatever
    /// the caller commits alongside them.
    /// </summary>
    internal void ApplyIdentity(string? resolvedName, double? population, IReadOnlyList<GeocodeCandidate>? candidates)
    {
        lock (_gate)
        {
            _identity = _identity.With(resolvedName, population, candidates);
        }
    }

    /// <summary>
    /// The display-state tie seam's identity half: the tied candidates become
    /// the dropdown, the queried name becomes the honest header (there is no
    /// winner to name; a blank query takes the neutral label), and the
    /// population clears. Runs under the gate so the reset is atomic with
    /// the state reset the caller commits alongside it.
    /// </summary>
    internal void ApplyTieIdentity(string? queriedLocation, IReadOnlyList<GeocodeCandidate> candidates)
    {
        lock (_gate)
        {
            _identity = _identity.With(string.IsNullOrWhiteSpace(queriedLocation) ? neutralLocationLabel : queriedLocation, 0, candidates);
        }
    }
}

/// <summary>The outcome of <see cref="WeatherResolution.Begin"/>.</summary>
internal enum BeginResult
{
    /// <summary>The claim was acquired; the caller runs the fetch and must
    /// call <see cref="WeatherResolution.End"/> in a finally.</summary>
    Started,

    /// <summary>Another fetch is already in flight — nothing to do.</summary>
    InFlight,

    /// <summary>The throttle window has not elapsed; the attempt cools down
    /// like a success.</summary>
    Throttled,
}
