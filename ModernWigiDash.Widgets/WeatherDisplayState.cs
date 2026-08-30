namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather widget's gated display state as ONE module: the single gate,
/// the snapshot state, the resolved-identity value (the shared
/// <see cref="WeatherResolutionState"/> — the dropdown candidates, the header
/// city name, the population), the pending resolved-label write-back, the
/// last-success stamp, and the forecast render copies — every read and
/// mutation runs under the same lock, so the "one consistent view" is a type,
/// not a discipline repeated at every call site. The widget's
/// <see cref="IWeatherFetchHost"/> seam bodies and the render tick's lock
/// region are forwards over this module, and the flow's test host wraps the
/// same module — the flow's guarantees are pinned against the production gate
/// shape, not a mirror of it.
/// </summary>
internal sealed class WeatherDisplayState
{
    /// <summary>
    /// The resolution inputs that force a re-fetch on change — an alias of
    /// <see cref="WeatherQueryKey.InvalidationProperties"/> (the owner of the
    /// set, ADR-0006: every key field except LocationMatch, which has its own
    /// branch in OnPropertyChanged). The drift test pins this set to the
    /// WeatherLocation record, so a new resolution input can never change
    /// the identity without a re-fetch.
    /// </summary>
    internal static readonly string[] ResolutionInvalidationProperties = WeatherQueryKey.InvalidationProperties;

    private readonly object _gate = new();
    private readonly string _neutralLabel;
    private readonly Func<DateTime> _now;
    private WeatherSnapshotState _state = new();
    // The widget twin's share of the invalidation rule: the shared
    // resolved-identity value (the client's fetch-control twin holds the same
    // type) plus the widget twin's unique field — the pending label
    // write-back. Both drop the shared value through
    // WeatherInvalidation.Drop, so the twins can never drift.
    private WeatherResolutionState _identity;
    private string? _pendingWriteback;
    private DateTime _lastSuccessFetchTime = DateTime.MinValue;
    private int _renderedForecastVersion = -1;
    private DailyForecastItem[] _dailySnapshot = [];
    private HourlyForecastItem[] _hourlySnapshot = [];

    /// <summary>
    /// <paramref name="neutralLocationLabel"/> is the identity's
    /// pre-resolution header and the post-drop header fallback (the widget
    /// passes the presentation's neutral-location label; the flow's test host
    /// passes its own). <paramref name="now"/> is the live UTC-time source,
    /// resolved AT STAMP TIME (not captured), so a test clock swap is
    /// observed by the last-success stamp.
    /// </summary>
    public WeatherDisplayState(string neutralLocationLabel, Func<DateTime> now)
    {
        _identity = new WeatherResolutionState(neutralLocationLabel, 0, []);
        _neutralLabel = neutralLocationLabel;
        _now = now;
    }

    /// <summary>The one gate (test seam: the flow's test host locks it to
    /// stamp a pre-await state directly).</summary>
    internal object Gate => _gate;

    /// <summary>The snapshot display state (swapped wholesale under the gate;
    /// the record itself is immutable).</summary>
    internal WeatherSnapshotState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>The shared resolved-identity value (the candidates, the
    /// header city name, the population) — read under the gate. The module
    /// hands out the immutable record, never a gate-bypassing mutator: the
    /// identity's transitions run only inside the module's gated members.</summary>
    internal WeatherResolutionState Identity
    {
        get { lock (_gate) { return _identity; } }
    }

    /// <summary>The pending resolved-label write-back awaiting the
    /// UI-thread flush — read under the gate (the queue and the take run
    /// under it, so a read in between is consistent).</summary>
    internal string? PendingLabelWriteback
    {
        get { lock (_gate) { return _pendingWriteback; } }
    }

    /// <summary>The last successful fetch's timestamp (the staleness
    /// display's input) — read under the gate, since the apply stamps it
    /// under the same gate.</summary>
    internal DateTime LastSuccessFetchTime
    {
        get { lock (_gate) { return _lastSuccessFetchTime; } }
    }

    /// <summary>The display state's data version — read under the gate,
    /// since the apply writes it under the same gate.</summary>
    internal int DataVersion
    {
        get { lock (_gate) { return _state.DataVersion; } }
    }

    /// <summary>
    /// The flow's apply seam: the policy's version-then-identity guard first,
    /// then the merge and the resolved-identity copies under ONE lock — an
    /// edit landing between the guard and the copies takes the same gate and
    /// wins (it either clears before the guard sees it or after the copies
    /// land), and the last-success stamp rides the same critical section.
    /// </summary>
    internal bool TryApply(WeatherApplyRequest request)
    {
        lock (_gate)
        {
            if (!WeatherSnapshotApplyPolicy.GuardsPass(request.ExpectedVersion, _state.DataVersion, request.IdentityGuard)) return false;
            _state = WeatherSnapshotApplyPolicy.Merge(request.Snapshot, _state);
            // The null-keeps replacement — the "response omitted this
            // section — keep the previous value" rule shared with the
            // snapshot merge (a provided population of 0 is the client's
            // no-data sentinel: it clears, it does not keep).
            _identity = _identity.With(request.ResolvedName, request.Population, request.Candidates);
            _lastSuccessFetchTime = _now();
            return true;
        }
    }

    /// <summary>
    /// The flow's tie seam: the identity guard must still pass (an edit that
    /// changed the resolution inputs since the fetch wins — the tie's
    /// candidates and header must not belong to the OLD identity), then one
    /// atomic step under the gate: the snapshot state resets to its
    /// placeholder (a tie has no data — a previous city's scalars must never
    /// render under a tie's header) with the data version bumped so the
    /// render model rebuilds and the forecast version bumped monotonically
    /// (a later re-apply must never land on a previously rendered version,
    /// or the capture's copy-skip would reuse the previous city's forecast
    /// lists), and the resolved-identity copies take the tied
    /// candidates (the Location Match dropdown), the queried name as the
    /// honest header (there is no winner to name), and a cleared population.
    /// <paramref name="queriedLocation"/> is read under the gate — one
    /// consistent view with the reset it labels.
    /// </summary>
    internal bool TryApplyTie(IReadOnlyList<GeocodeCandidate> candidates, Func<bool> identityGuard, Func<string?> queriedLocation)
    {
        lock (_gate)
        {
            if (!identityGuard()) return false;
            // The forecast version bumps too — the reset must stay off every
            // previously rendered version, or a re-apply could land on one
            // and the capture's copy-skip would hand out the previous
            // city's forecast lists under the new city's header.
            _state = new WeatherSnapshotState
            {
                DataVersion = _state.DataVersion + 1,
                ForecastVersion = _state.ForecastVersion + 1,
            };
            string? location = queriedLocation();
            _identity = _identity.With(string.IsNullOrWhiteSpace(location) ? _neutralLabel : location, 0, candidates);
            return true;
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
    /// The single edit-path invalidation, per drop kind, under the gate (so
    /// the clear is atomic against an in-flight fetch's apply): the pending
    /// write-back drops (an edit landing after a completed fetch must not be
    /// overwritten by the old identity's label on the next render), and the
    /// shared identity value drops through the single rule — the Location
    /// Match pick (Coordinates kind) keeps the candidates it was offered from
    /// while the old winner's name + population void; every other resolution
    /// input (Location kind) voids the whole identity so the render-model
    /// cache key turns and the header drops the old city immediately.
    /// </summary>
    internal void Invalidate(WeatherInvalidationKind kind)
    {
        lock (_gate)
        {
            _pendingWriteback = null;
            _identity = WeatherInvalidation.Drop(kind, _identity);
        }
    }

    /// <summary>
    /// Test seam: replaces the state wholesale under the gate (the
    /// boot-load version-guard test stamps the pre-await state directly).
    /// </summary>
    internal void ReplaceState(WeatherSnapshotState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    /// <summary>
    /// One consistent view of the display state for the render tick: the
    /// forecast-list copies refresh only when the source's version actually
    /// changed (the copies are skipped on the frames in between), the state
    /// scalars and the resolved identity are read from that ONE value, and
    /// the whole render-model input is assembled under the gate — the build
    /// module receives a torn-write-free view (the per-frame inputs are
    /// always assembled; the key-hit saves the model build, not the inputs).
    /// </summary>
    internal (WeatherRenderModelInputs Inputs, DateTime LastSuccessFetchTime) CaptureRenderView(
        SKRect bounds, WeatherHeaderLayout header, float scale,
        string layoutMode, string unitSystem, string customLabel, bool hideLocation,
        bool showFeelsLike, bool showHumidity, bool showWind, bool showHighLow, bool showForecast,
        string location)
    {
        lock (_gate)
        {
            if (_renderedForecastVersion != _state.ForecastVersion)
            {
                _renderedForecastVersion = _state.ForecastVersion;
                _dailySnapshot = _state.DailyForecasts.ToArray();
                _hourlySnapshot = _state.HourlyForecasts.ToArray();
            }
            WeatherSnapshotState state = _state;
            WeatherRenderModelInputs inputs = new(
new WeatherRenderModelKey(
                    state.DataVersion, bounds,
                    layoutMode, unitSystem, customLabel, _identity.ResolvedName,
                    showFeelsLike, showHumidity, showWind, showHighLow, showForecast,
                    hideLocation,
                    _identity.Candidates.Count,
                    state.HasData,
                    LocationSet: !string.IsNullOrWhiteSpace(location)),
                state.WeatherCode, state.IsDay, state.CurrentTempC, state.FeelsLikeC, state.Humidity,
                state.WindSpeedKmH, state.HighTempC, state.LowTempC,
_dailySnapshot, _hourlySnapshot,
                header, scale,
                location, _identity.Candidates.Count,
                _neutralLabel);
            return (inputs, _lastSuccessFetchTime);
        }
    }
}
