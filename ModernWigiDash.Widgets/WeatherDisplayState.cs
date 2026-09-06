namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather widget's gated display state as ONE module: the single gate,
/// the snapshot state, and the forecast render copies — every read and
/// mutation runs under the same lock, so the "one consistent view" is a type,
/// not a discipline repeated at every call site. The resolved identity and
/// the pending label write-back live in the cluster's one identity owner
/// (<see cref="WeatherResolution"/>), which this module composes with its own
/// snapshot state under its own gate — the flow's apply/tie seams commit both
/// halves atomically (the identity half under the resolution's gate, the
/// snapshot half under this gate). The widget's
/// <see cref="IWeatherFetchHost"/> seam bodies and the render tick's lock
/// region are forwards over this module, and the flow's test host wraps the
/// same module — the flow's guarantees are pinned against the production gate
/// shape, not a mirror of it.
/// </summary>
internal sealed class WeatherDisplayState(WeatherResolution resolution, string neutralLocationLabel, Func<DateTime> now)
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
    private readonly Func<DateTime> _now = now;
    private WeatherSnapshotState _state = new();
    private DateTime _lastSuccessFetchTime = DateTime.MinValue;
    private int _renderedForecastVersion = -1;
    private DailyForecastItem[] _dailySnapshot = [];
    private HourlyForecastItem[] _hourlySnapshot = [];

    /// <summary>The cluster's one resolved-identity owner (the shared
    /// identity value, the fetch-side fields, the pending write-back).</summary>
    internal WeatherResolution Resolution => resolution;

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
    /// header city name, the population) — forwarded from the identity
    /// owner's gated read.</summary>
    internal WeatherResolutionState Identity => resolution.Identity;

    /// <summary>The pending resolved-label write-back awaiting the
    /// UI-thread flush — forwarded from the identity owner's gated read.</summary>
    internal string? PendingLabelWriteback => resolution.PendingLabelWriteback;

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
    /// then the merge and the resolved-identity copies — the snapshot half
    /// under THIS gate, the identity half under the resolution's gate (the
    /// two gates are taken in one sequence; an edit landing between them wins
    /// on whichever side it touches, and the guard re-reads the live location
    /// so the other side's copy is vetoed there). The last-success stamp rides
    /// the snapshot critical section.
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
            resolution.ApplyIdentity(request.ResolvedName, request.Population, request.Candidates);
            _lastSuccessFetchTime = _now();
            return true;
        }
    }

    /// <summary>
    /// The flow's tie seam: the identity guard must still pass (an edit that
    /// changed the resolution inputs since the fetch wins — the tie's
    /// candidates and header must not belong to the OLD identity), then one
    /// atomic step per gate: the snapshot state resets to its placeholder (a
    /// tie has no data — a previous city's scalars must never render under a
    /// tie's header) with the data version bumped so the render model rebuilds
    /// and the forecast version bumped monotonically (a later re-apply must
    /// never land on a previously rendered version, or the capture's
    /// copy-skip would reuse the previous city's forecast lists), and the
    /// resolved-identity copies take the tied candidates (the Location Match
    /// dropdown), the queried name as the honest header (there is no winner
    /// to name), and a cleared population.
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
            resolution.ApplyTieIdentity(location, candidates);
            return true;
        }
    }

    /// <summary>
    /// The single edit-path invalidation, per drop kind: the shared identity
    /// value, the coordinates, the query, the throttle, AND the pending
    /// write-back all drop through the identity owner's one gated entry —
    /// the Location Match pick (Coordinates kind) keeps the candidates it was
    /// offered from while the old winner's name + population void; every
    /// other resolution input (Location kind) voids the whole identity so the
    /// render-model cache key turns and the header drops the old city
    /// immediately. One application site, by construction.
    /// </summary>
    internal void Invalidate(WeatherInvalidationKind kind) => resolution.Invalidate(kind);

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
            WeatherResolutionState identity = resolution.Identity;
            WeatherRenderModelInputs inputs = new(
 new WeatherRenderModelKey(
                    state.DataVersion, bounds,
                    layoutMode, unitSystem, customLabel, identity.ResolvedName,
                    showFeelsLike, showHumidity, showWind, showHighLow, showForecast,
                    hideLocation,
                    identity.Candidates.Count,
                    state.HasData,
                    LocationSet: !string.IsNullOrWhiteSpace(location)),
                state.WeatherCode, state.IsDay, state.CurrentTempC, state.FeelsLikeC, state.Humidity,
                state.WindSpeedKmH, state.HighTempC, state.LowTempC,
_dailySnapshot, _hourlySnapshot,
                header, scale,
                location, identity.Candidates.Count,
                neutralLocationLabel);
            return (inputs, _lastSuccessFetchTime);
        }
    }
}
