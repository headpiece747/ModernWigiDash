namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather fetch-flow module: the SEQUENCE around one fetch — the identity
/// key captured before the await, the outcome verification (through
/// <see cref="WeatherQueryKey.SameKey"/>, the ADR-0006 predicate, never a
/// second spelling), the post-await re-validation, the drop-and-refetch
/// routing, the write-back gating, the cadence gate, and the boot-load
/// rollback. The widget keeps only the host concerns — the property coercion
/// (BuildLocation), the gate discipline around the display state (the apply
/// seam runs under <c>_forecastGate</c>), the UI-thread write-back flush, and
/// the context requests — and passes them in as seams. Everything else of the
/// former <c>FetchLiveWeatherAsync</c> sequence (150 lines of widget code
/// across five methods) lives here, testable through this interface without
/// a widget instance, an HTTP stub, or a render tick.
/// <para>
/// The caller's obligation is one line: ask <see cref="CanFetch"/>, run
/// <see cref="RunFetchAsync"/>. The capture-window order (key captured before
/// the await, re-validated after — twice: against the outcome key and against
/// the live location) is enforced here, not in comments at a call site.
/// </para>
/// </summary>
internal sealed class WeatherFetchFlow
{
    private readonly WeatherClient _client;
    private readonly WeatherResolvedIdentity _identity;
    private readonly Func<WeatherLocation> _currentLocation;
    private readonly Func<WeatherSnapshot, int?, Func<bool>?, IReadOnlyList<GeocodeCandidate>?, double?, string?, bool> _applySnapshot;
    private readonly Func<int> _dataVersion;
    private readonly Func<bool> _isStaticSnapshot;
    private readonly Func<CancellationToken> _runToken;
    private readonly Action<Func<bool>, string> _setPendingWritebackIfCurrent;
    private readonly Action _requestRender;
    private readonly Action _requestInspectorRefresh;

    private string _lastInspectorCandidatesStamp = "";

    /// <summary>Test seam: replaces the client's cache-load leg so the boot
    /// race (version + identity guards) is drivable without a file.</summary>
    internal Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride { get; set; }

    /// <summary>
    /// The primary constructor: the client (fetch/load legs, throttle truth,
    /// rollback) and the identity module (resolved-name copies, candidates,
    /// pending write-back) are the cluster's real modules; the widget-bound
    /// seams carry the host concerns across the interface.
    /// </summary>
    /// <param name="applySnapshot">The gated apply: under the widget's gate it
    /// runs the <see cref="WeatherSnapshotApplyPolicy"/> guard + merge and the
    /// identity's apply as one atomic step (the guard re-checks the identity).
    /// Returns whether the snapshot was applied.</param>
    /// <param name="dataVersion">The display state's data version, read under
    /// the widget's gate (the boot load's torn-write guard).</param>
    /// <param name="setPendingWritebackIfCurrent">Sets the pending label
    /// write-back under the widget's gate, only when the identity guard
    /// passes — the check + set are one critical section, the same rule the
    /// old inline lock spelled.</param>
    internal WeatherFetchFlow(
        WeatherClient client,
        WeatherResolvedIdentity identity,
        Func<WeatherLocation> currentLocation,
        Func<WeatherSnapshot, int?, Func<bool>?, IReadOnlyList<GeocodeCandidate>?, double?, string?, bool> applySnapshot,
        Func<int> dataVersion,
        Func<bool> isStaticSnapshot,
        Func<CancellationToken> runToken,
        Action<Func<bool>, string> setPendingWritebackIfCurrent,
        Action requestRender,
        Action requestInspectorRefresh)
    {
        _client = client;
        _identity = identity;
        _currentLocation = currentLocation;
        _applySnapshot = applySnapshot;
        _dataVersion = dataVersion;
        _isStaticSnapshot = isStaticSnapshot;
        _runToken = runToken;
        _setPendingWritebackIfCurrent = setPendingWritebackIfCurrent;
        _requestRender = requestRender;
        _requestInspectorRefresh = requestInspectorRefresh;
    }

    /// <summary>
    /// The single "fetch if due" gate for every cadence source (the refresh
    /// PollLoop, the render kick, the touch refresh, the edit-time force):
    /// the static-snapshot rule (a frozen snapshot is never re-fetched on a
    /// non-forced cadence after a boot load stamped the throttle) and the
    /// client's throttle window are applied here, once — the caller neither
    /// re-derives the policy nor reads the client's throttle state.
    /// </summary>
    internal bool CanFetch(bool force)
    {
        if (force) return true;
        if (_isStaticSnapshot() && _client.LastFetchTimeUtc != DateTime.MinValue) return false;
        return _client.IsFetchWindowElapsed();
    }

    /// <summary>
    /// One run of the fetch flow: capture the identity key, fetch through the
    /// client, verify the outcome against the key (Stale verdict, outcome key
    /// through <see cref="WeatherQueryKey.SameKey"/>, live re-check), apply or
    /// drop, gate the resolved-label write-back, and refresh the inspector
    /// only when the pickable candidates changed. Returns the flow's verdict
    /// (<see cref="WeatherFetchFlowOutcome"/>).
    /// </summary>
    internal async Task<WeatherFetchFlowOutcome> RunFetchAsync(bool force = false)
    {
        // The query key at START: the client's Stale verdict covers its whole
        // capture window (through the cache-save await), but this key still
        // guards the outcome-key comparison below (a resolution-input change
        // landing between this capture and the client's own capture resolves
        // a DIFFERENT identity) and the post-await gap re-check.
        string fetchKey = WeatherQueryKey.Build(_currentLocation());
        WeatherFetchResult result;
        try
        {
            result = await _client.FetchCurrentAsync(_currentLocation(), force, _runToken()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Teardown: the widget's poll CTS was cancelled (dispose) — a
            // cancelled fetch is not a failure, so nothing is logged or applied.
            return WeatherFetchFlowOutcome.Cancelled;
        }

        if (result is WeatherFetchResult.Stale)
        {
            // The resolution identity changed while the fetch was in flight
            // (the widget's invalidation cleared the client's query identity):
            // the client dropped the stale result — weather AND label — without
            // stamping the throttle. Re-fetch the new identity immediately,
            // since the edit-time force refresh was swallowed by the in-flight
            // claim, which this fetch's completion has now released.
            _ = RunFetchAsync(force: true);
            return WeatherFetchFlowOutcome.DroppedStale;
        }

        // The outcome carries the key the client actually resolved for. It
        // must match the key captured here: a resolution-input change landing
        // between this capture and the client's own capture resolves a
        // DIFFERENT identity, and if that identity was then changed back
        // before this continuation runs the live check below cannot see it —
        // the outcome key can. Dropped through the ADR-0006 predicate (the ONE
        // spelling — never an inline ordinal compare). Drop the result —
        // weather AND label — when either comparison fails.
        if (result is WeatherFetchResult.Fetched fetchedKeyCheck
            && !WeatherQueryKey.SameKey(fetchedKeyCheck.QueryKey, fetchKey))
        {
            _ = RunFetchAsync(force: true);
            return WeatherFetchFlowOutcome.DroppedStale;
        }

        // Post-await re-validation: the client's Stale verdict closes its own
        // capture window (through the cache-save await); an identity change
        // landing in the return-to-apply gap the client's window cannot see
        // (including the post-InitializeAsync profile hydration) still comes
        // back as Fetched here. Drop the result — weather AND label.
        if (!StillCurrent(fetchKey))
        {
            _ = RunFetchAsync(force: true);
            return WeatherFetchFlowOutcome.DroppedStale;
        }

        if (result is not WeatherFetchResult.Fetched fetched)
        {
            // Throttled / InFlight / Failed: keep the previous state silently.
            return WeatherFetchFlowOutcome.Skipped;
        }

        // The apply is identity-guarded under the same lock as the version
        // checks: an edit landing between the post-await re-check above and
        // this point must win — the snapshot and the resolved-identity copies
        // must not belong to the OLD identity (the stale write-back is
        // protected separately below).
        if (!_applySnapshot(fetched.Snapshot, null, () => StillCurrent(fetchKey),
                fetched.Candidates, fetched.Population, fetched.Snapshot.ResolvedCityName))
        {
            _ = RunFetchAsync(force: true);
            return WeatherFetchFlowOutcome.DroppedStale;
        }

        WeatherSnapshot snapshot = fetched.Snapshot;

        // The resolved label's write-back is deferred to the UI thread (the
        // widget's Render flushes the pending field, so Context.PersistProperty
        // stays on the UI thread). It is skipped entirely when a CustomLabel
        // supplies the title: the label is display-only, and writing it into
        // Location would destroy the query (explicit-coords/pick + CustomLabel
        // would overwrite "New York" with "Home" in the profile). The identity
        // is re-validated at the set, under the same gate the edit-side clears
        // use: either the gated set lands before the edit's gated clear (the
        // clear erases it) or after (the guard re-reads the new location and
        // the set never happens).
        bool writebackEligible = !string.IsNullOrWhiteSpace(snapshot.ResolvedCityName)
            && string.IsNullOrWhiteSpace(_currentLocation().CustomLabel)
            && !string.Equals(snapshot.ResolvedCityName, _currentLocation().Location, StringComparison.Ordinal);
        if (writebackEligible)
        {
            _setPendingWritebackIfCurrent(() => StillCurrent(fetchKey), snapshot.ResolvedCityName);
        }

        // The geocode may have produced new Location Match candidates: refresh
        // the inspector so an already-open panel shows the dropdown (the
        // Twitch pattern — the inspector only builds the editor when options
        // exist). Only when the option set changed — see the stamp below.
        string stamp = string.Join('\n', _identity.Candidates.Select(c => c.Query));
        if (!string.Equals(stamp, _lastInspectorCandidatesStamp, StringComparison.Ordinal))
        {
            _lastInspectorCandidatesStamp = stamp;
            _requestInspectorRefresh();
        }

        _requestRender();
        return WeatherFetchFlowOutcome.Applied;
    }

    /// <summary>
    /// The boot cache load. The data version is captured BEFORE the await and
    /// the identity guard re-checks after, so a fetch that landed while the
    /// load was in flight (InitializeAsync fires the load and the boot fetch
    /// concurrently) can never be overwritten by the stale cache, and a
    /// hydration that changed the location wins over the default-stamped
    /// cache. A discarded load rolls the client's committed identity state
    /// back (the load's interface contract: it committed; the rejection is
    /// the caller's job) — when the discard was an identity change, a
    /// version-only skip leaves nothing to undo.
    /// </summary>
    internal async Task RunBootLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            int versionBefore = _dataVersion();
            string locationKeyBefore = WeatherQueryKey.Build(_currentLocation());

            // The cache is identity-checked against the CURRENT location by
            // the client itself (a cache saved for a different resolution
            // must not surface as fresh weather).
            var load = CacheLoadOverride ?? _client.LoadCacheAsync;
            var cached = await load(_currentLocation(), cancellationToken).ConfigureAwait(false);
            if (cached is null) return;

            // The version + identity guards run inside the apply seam's lock:
            // a fetch that landed during the await (version) or a hydration
            // that changed the location (identity) both win over the stale
            // cache, and the check + apply are one atomic step. The cache
            // cannot carry candidates or population (they stay null — the
            // policy's keep-previous rule — exactly like the client's own
            // load state).
            bool applied = _applySnapshot(cached, versionBefore, () => StillCurrent(locationKeyBefore),
                null, null, cached.ResolvedCityName);
            if (!applied && !StillCurrent(locationKeyBefore))
            {
                // The load already committed its resolution state (name/lat/
                // lon/throttle) — an identity change means the discard must
                // roll that back so the next resolution starts clean. A
                // version-only skip means a fresh fetch already landed: the
                // identity still matches, so there is nothing to undo.
                _client.InvalidateCoordinates();
            }
        }
        catch (OperationCanceledException)
        {
            // Teardown: the poll token was cancelled (dispose) — a cancelled
            // cache load is not a failure, so nothing is logged or applied.
        }
    }

    /// <summary>
    /// The single spelling of "the resolution identity still matches the key
    /// captured at fetch start": re-derives the LIVE key from the current
    /// location through the ADR-0006 predicate. Every await boundary of this
    /// module re-validates through this — one comparison shape.
    /// </summary>
    private bool StillCurrent(string key)
        => WeatherQueryKey.SameKey(key, WeatherQueryKey.Build(_currentLocation()));
}