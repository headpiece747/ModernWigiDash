namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather fetch-flow module: the SEQUENCE around one fetch — the identity
/// key captured before the await, the outcome verification (through
/// <see cref="WeatherQueryKey.SameKey"/>, the ADR-0006 predicate, never a
/// second spelling), the post-await re-validation, the drop-and-refetch
/// routing, the write-back gating, the cadence gate, and the boot-load
/// rollback. The host keeps only the host concerns — the property coercion
/// (BuildLocation), the gate discipline around the display state (the apply
/// and the version read run under the host's gate), the UI-thread write-back
/// flush, and the context requests — and hands them to this module through
/// the <see cref="IWeatherFetchHost"/> seam: one named seam instead of a bag
/// of anonymous delegate parameters, so the gate discipline is a type and a
/// doc at the seam, not comments repeated at every wiring site. Everything
/// else of the former <c>FetchLiveWeatherAsync</c> sequence (150 lines of
/// widget code across five methods) lives here, testable through this
/// interface without a widget instance, an HTTP stub, or a render tick.
/// <para>
/// The caller's obligation is one line: ask <see cref="CanFetch"/>, run
/// <see cref="RunFetchAsync"/>. The capture-window order (key captured before
/// the await, re-validated after — twice: against the outcome key and against
/// the live location) is enforced here, not in comments at a call site.
/// </para>
/// <param name="client">The cluster's data module: the resolve/fetch/cache
/// legs, the throttle truth, and the discarded-load rollback.</param>
/// <param name="identity">The widget-side resolved-identity twin (the
/// dropdown candidates, the resolved population and name, the pending
/// write-back).</param>
/// <param name="host">The host seam: the property coercion, the gate around
/// the display state (a named <c>TryApply</c> <c>WeatherApplyRequest</c>
/// seam), the write-back guard, and the context requests. The widget is the
/// production adapter; the flow's tests carry an adapter over the same
/// seam.</param>
/// </summary>
internal sealed class WeatherFetchFlow(WeatherClient client, WeatherResolvedIdentity identity, IWeatherFetchHost host)
{
    private string _lastInspectorCandidatesStamp = "";

    /// <summary>Test seam: replaces the client's cache-load leg so the boot
    /// race (version + identity guards) is drivable without a file.</summary>
    internal Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride { get; set; }

    /// <summary>
    /// The single "fetch if due" gate for every cadence source (the refresh
    /// PollLoop, the render kick, the touch refresh, the edit-time force):
    /// the static-snapshot rule (a frozen snapshot is never re-fetched on a
    /// non-forced cadence once a fetch stamp exists) and the client's throttle
    /// window are applied here, once — the caller neither re-derives the
    /// policy nor reads the client's throttle state (the stamp is read as the
    /// client's <see cref="WeatherClient.HasFetched"/> fact, never as a raw
    /// timestamp).
    /// </summary>
    internal bool CanFetch(bool force)
    {
        if (force) return true;
        if (host.IsStaticSnapshot && client.HasFetched) return false;
        return client.IsFetchWindowElapsed();
    }

    /// <summary>
    /// One run of the fetch flow: capture the identity key, fetch through the
    /// <see cref="WeatherClient"/>, verify the outcome against the captured
    /// key and the live location, and only then apply the snapshot under the
    /// host's gate. The <see cref="WeatherFetchFlowOutcome"/> reports what
    /// happened to this fetch's outcome.
    /// </summary>
    internal async Task<WeatherFetchFlowOutcome> RunFetchAsync(bool force = false)
    {
        // The query key at START: the client's Stale verdict covers its whole
        // capture window (through the cache-save await), but this key still
        // guards the outcome-key comparison below (a resolution-input change
        // landing between this capture and the client's own capture resolves
        // a DIFFERENT identity) and the post-await gap re-check.
        string fetchKey = WeatherQueryKey.Build(host.CurrentLocation);
        WeatherFetchResult result;
        try
        {
            result = await client.FetchCurrentAsync(host.CurrentLocation, force, host.RunToken).ConfigureAwait(false);
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

        // The outcome carries the key the client actually resolved for (both
        // Fetched and Tie — a tie's candidates belong to the identity that
        // produced them, exactly like a snapshot). It must match the key
        // captured here: a resolution-input change landing between this
        // capture and the client's own capture resolves a DIFFERENT identity,
        // and if that identity was then changed back before this continuation
        // runs the live check below cannot see it — the outcome key can.
        // Dropped through the ADR-0006 predicate (the ONE spelling — never an
        // inline ordinal compare). Drop the result — weather, label, AND
        // candidates — when the comparison fails.
        string? carriedKey = result switch
        {
            WeatherFetchResult.Fetched fetchedKeyCheck => fetchedKeyCheck.QueryKey,
            WeatherFetchResult.Tie tiedKeyCheck => tiedKeyCheck.QueryKey,
            _ => null,
        };
        if (carriedKey is not null && !WeatherQueryKey.SameKey(carriedKey, fetchKey))
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

        // The apply is identity-guarded under the same lock as the version
        // checks: an edit landing between the post-await re-check above and
        // this point must win — the applied state (a snapshot, or a tie's
        // candidates + header) must not belong to the OLD identity (the stale
        // write-back is protected separately below).
        bool appliedTie = false;
        if (result is WeatherFetchResult.Tie tie)
        {
            // A tie has no snapshot to apply — the host's tie seam resets the
            // data state to its placeholder and applies the tied candidates
            // (the dropdown) plus the queried header. No label write-back:
            // there is no resolved city to persist, and writing the raw query
            // back into Location would be a no-op at best.
            if (!host.TryApplyTie(tie.Candidates, () => StillCurrent(fetchKey)))
            {
                _ = RunFetchAsync(force: true);
                return WeatherFetchFlowOutcome.DroppedStale;
            }
            appliedTie = true;
        }
        else if (result is WeatherFetchResult.Fetched fetched)
        {
            if (!host.TryApply(new WeatherApplyRequest(fetched.Snapshot, null, () => StillCurrent(fetchKey),
                    fetched.Candidates, fetched.Population, fetched.Snapshot.ResolvedCityName)))
            {
                _ = RunFetchAsync(force: true);
                return WeatherFetchFlowOutcome.DroppedStale;
            }

            WeatherSnapshot snapshot = fetched.Snapshot;

            // The resolved label's write-back is deferred to the UI thread (the
            // host's render flushes the pending field, so the host's persistence
            // stays on the UI thread). It is skipped entirely when a CustomLabel
            // supplies the title: the label is display-only, and writing it into
            // Location would destroy the query (explicit-coords/pick + CustomLabel
            // would overwrite "New York" with "Home" in the profile). The identity
            // is re-validated at the set, under the same gate the edit-side clears
            // use: either the gated set lands before the edit's gated clear (the
            // clear erases it) or after (the guard re-reads the new location and
            // the set never happens).
            bool writebackEligible = !string.IsNullOrWhiteSpace(snapshot.ResolvedCityName)
                && string.IsNullOrWhiteSpace(host.CurrentLocation.CustomLabel)
                && !string.Equals(snapshot.ResolvedCityName, host.CurrentLocation.Location, StringComparison.Ordinal);
            if (writebackEligible)
            {
                host.QueueLabelWriteback(() => StillCurrent(fetchKey), snapshot.ResolvedCityName);
            }
        }
        else
        {
            // Throttled / InFlight / Failed: keep the previous state silently.
            return WeatherFetchFlowOutcome.Skipped;
        }

        // The geocode may have produced new Location Match candidates — either
        // a fresh resolution's candidate list or a tie's tied options: refresh
        // the inspector so an already-open panel shows the dropdown (the
        // Twitch pattern — the inspector only builds the editor when options
        // exist). Only when the option set changed — see the stamp below.
        string stamp = string.Join('\n', identity.Candidates.Select(c => c.Query));
        if (!string.Equals(stamp, _lastInspectorCandidatesStamp, StringComparison.Ordinal))
        {
            _lastInspectorCandidatesStamp = stamp;
            host.RequestInspectorRefresh();
        }

        host.RequestRender();
        return appliedTie ? WeatherFetchFlowOutcome.AppliedTie : WeatherFetchFlowOutcome.Applied;
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
            int versionBefore = host.DataVersion;
            string locationKeyBefore = WeatherQueryKey.Build(host.CurrentLocation);

            // The cache is identity-checked against the CURRENT location by
            // the client itself (a cache saved for a different resolution
            // must not surface as fresh weather).
            var load = CacheLoadOverride ?? client.LoadCacheAsync;
            var cached = await load(host.CurrentLocation, cancellationToken).ConfigureAwait(false);
            if (cached is null) return;

            // The version + identity guards run inside the apply seam's lock:
            // a fetch that landed during the await (version) or a hydration
            // that changed the location (identity) both win over the stale
            // cache, and the check + apply are one atomic step. The cache
            // cannot carry candidates or population (they stay null — the
            // policy's keep-previous rule — exactly like the client's own
            // load state).
            bool applied = host.TryApply(new WeatherApplyRequest(cached, versionBefore, () => StillCurrent(locationKeyBefore),
                null, null, cached.ResolvedCityName));
            if (!applied && !StillCurrent(locationKeyBefore))
            {
                // The load already committed its resolution state (name/lat/
                // lon/throttle) — an identity change means the discard must
                // roll that back so the next resolution starts clean. A
                // version-only skip means a fresh fetch already landed: the
                // identity still matches, so there is nothing to undo.
                client.InvalidateCoordinates();
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
        => WeatherQueryKey.SameKey(key, WeatherQueryKey.Build(host.CurrentLocation));
}