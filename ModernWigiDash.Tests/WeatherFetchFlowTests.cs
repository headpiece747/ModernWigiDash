using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The fetch-flow module's interface-level pins (C1): the sequence the
/// former <c>FetchLiveWeatherAsync</c> spelled across five widget methods —
/// the key capture, the two drop gates (outcome key vs. start key, live
/// re-check vs. post-await), the forced re-fetch routing, the write-back
/// gating, the cadence gate, and the boot-load rollback — asserted through
/// <see cref="WeatherFetchFlow"/> without a widget instance, a render tick,
/// or the widget's gate. The host harness mirrors the widget's real seam
/// wiring (the same apply policy, the same identity module, the same
/// gate discipline), so a rule the flow gets wrong fails here and in the
/// widget identically.
/// </summary>
[TestClass]
public class WeatherFetchFlowTests
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "wmd-weather-flow-tests");

    private static readonly WeatherLocation NycCoords = new("Fixed Location", "40.71,-74.00", null, null, null);
    private static readonly WeatherLocation LondonCoords = new("Fixed Location", "51.51,-0.13", null, null, null);
    private static readonly WeatherLocation BerlinCity = new("City", "Berlin", null, null, null);
    private static readonly WeatherLocation BerlinHome = new("City", "Berlin", null, null, "Home");

    /// <summary>The NYC leg's weather (distinguishable from Berlin/London by
    /// temperature, so an applied vs. dropped fetch is observable).</summary>
    private const string NycForecastJson = """
        {
          "latitude": 40.7128, "longitude": -74.006,
          "current": { "temperature_2m": 11.1, "relative_humidity_2m": 50, "apparent_temperature": 8.8, "weather_code": 63, "wind_speed_10m": 5.2, "time": "2026-08-07T12:00" }
        }
        """;

    private const string BerlinForecastJson = """
        {
          "latitude": 52.52, "longitude": 13.405,
          "current": { "temperature_2m": 22.2, "relative_humidity_2m": 40, "apparent_temperature": 20.5, "weather_code": 2, "wind_speed_10m": 12.3, "time": "2026-08-07T12:00" }
        }
        """;

    private const string LondonForecastJson = """
        {
          "latitude": 51.51, "longitude": -0.13,
          "current": { "temperature_2m": 33.3, "relative_humidity_2m": 55, "apparent_temperature": 31.0, "weather_code": 61, "wind_speed_10m": 7.7, "time": "2026-08-07T12:00" }
        }
        """;

    private static HttpResponseMessage FlowRespond(HttpRequestMessage request)
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleGeocode);
        if (url.Contains("latitude=40.7100", StringComparison.Ordinal)) return StubHttpHandler.Ok(NycForecastJson);
        if (url.Contains("latitude=52.5200", StringComparison.Ordinal)) return StubHttpHandler.Ok(BerlinForecastJson);
        if (url.Contains("latitude=51.5100", StringComparison.Ordinal)) return StubHttpHandler.Ok(LondonForecastJson);
        return StubHttpHandler.NotFound();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* best-effort */ }
    }

    private static string NewCacheDir() => Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

    private FlowHost NewHost(StubHttpHandler stub, FakeTimeProvider? clock = null, Func<WeatherLocation>? locationSeam = null)
    {
        var host = new FlowHost { LocationSeam = locationSeam };
        host.Client = new WeatherClient(NewCacheDir(), "weather_flow.json", timeProvider: clock, http: new HttpClient(stub));
        host.Flow = new WeatherFetchFlow(host.Client, host.Identity, host);
        return host;
    }

    // -- RunFetchAsync: the applied path --------------------------------------

    [TestMethod]
    public async Task RunFetchAsync_FetchedOutcome_AppliesSnapshotAndRequestsRender()
    {
        var stub = new StubHttpHandler(FlowRespond);
        var host = NewHost(stub);

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, outcome);
        Assert.AreEqual(11.1, host.State.CurrentTempC);
        Assert.AreEqual(1, host.State.DataVersion);
        Assert.AreEqual(1, host.RenderRequests);
        Assert.AreEqual("40.71, -74.00", host.Identity.CityName);
        Assert.AreEqual(1, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "A coordinate-pair location must skip the geocode leg.");
    }

    [TestMethod]
    public async Task RunFetchAsync_CityResolution_SetsPendingLabelWriteback()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        host.Location = BerlinCity;

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, outcome);
        Assert.IsFalse(string.IsNullOrEmpty(host.Identity.PendingWriteback),
            "A resolved label that differs from the raw query must queue the write-back.");
        Assert.AreEqual(host.Identity.CityName, host.Identity.PendingWriteback,
            "The write-back must carry the SAME label the header shows — one label source.");
        Assert.AreNotEqual("Berlin", host.Identity.PendingWriteback);
    }

    [TestMethod]
    public async Task RunFetchAsync_CustomLabel_SuppressesLabelWriteback()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        host.Location = BerlinHome;

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, outcome);
        Assert.IsNull(host.Identity.PendingWriteback,
            "A CustomLabel is display-only: writing the resolved label into Location would destroy the query.");
    }

    // -- RunFetchAsync: the drop gates ----------------------------------------

    [TestMethod]
    public async Task RunFetchAsync_PostAwaitIdentityChange_DropsResultAndForceRefetchesLiveLocation()
    {
        FlowHost? host = null;
        // The edit lands in the return-to-apply gap — AFTER the client's own
        // capture window closed (the client's Stale verdict cannot see it,
        // so its outcome still comes back Fetched): the flow's post-await
        // live re-check is the gate that must drop the old identity's result.
        var stub = new StubHttpHandler(request =>
        {
            if (request.RequestUri is { AbsoluteUri: string url } && url.Contains("/v1/forecast", StringComparison.Ordinal))
            {
                host!.Location = BerlinCity;
            }
            return FlowRespond(request);
        });
        host = NewHost(stub);

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.DroppedStale, outcome);
        Assert.AreEqual(25.0, host.State.CurrentTempC, "The dropped result must never reach the display state.");
        // The forced re-fetch is fire-and-forget: wait for the NEW identity
        // to land, and pin that the drop triggered a second forecast leg.
        await TestWait.WaitUntilAsync(() => Math.Abs(host.State.CurrentTempC - 22.2) < 1e-9, TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)));
        StringAssert.StartsWith(host.Identity.CityName, "Berlin");
    }

    [TestMethod]
    public async Task RunFetchAsync_OutcomeKeyMismatch_DropsResultAndForceRefetches()
    {
        // The flow reads the current location twice around the fetch (the key
        // capture, then the fetch call). An edit landing exactly between
        // those two reads resolves a DIFFERENT identity the live re-check
        // cannot see (the live location is the NEW one when the continuation
        // runs) — the outcome-key vs. start-key comparison rides
        // WeatherQueryKey.SameKey and is the gate that catches it.
        int seamCalls = 0;
        var host = NewHost(new StubHttpHandler(FlowRespond),
            locationSeam: () => Interlocked.Increment(ref seamCalls) == 1 ? NycCoords : LondonCoords);

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.DroppedStale, outcome);
        Assert.AreEqual(25.0, host.State.CurrentTempC, "The mismatched-identity result must never reach the display state.");
        await TestWait.WaitUntilAsync(() => Math.Abs(host.State.CurrentTempC - 33.3) < 1e-9, TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RunFetchAsync_FailedOutcome_SkipsAndKeepsPreviousState()
    {
        var host = NewHost(new StubHttpHandler(_ => StubHttpHandler.NotFound()));

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.Skipped, outcome);
        Assert.AreEqual(0, host.State.DataVersion);
        Assert.AreEqual(25.0, host.State.CurrentTempC);
        Assert.AreEqual(0, host.RenderRequests);
    }

    [TestMethod]
    public async Task RunFetchAsync_SecondRunWithinThrottleWindow_SkipsWithoutHttp()
    {
        var stub = new StubHttpHandler(FlowRespond);
        var host = NewHost(stub);

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, await host.Flow.RunFetchAsync());
        int callsAfterFirst = stub.Calls;

        var outcome = await host.Flow.RunFetchAsync(force: false);

        Assert.AreEqual(WeatherFetchFlowOutcome.Skipped, outcome);
        Assert.AreEqual(callsAfterFirst, stub.Calls, "A throttled run must not touch the wire.");
    }

    [TestMethod]
    public async Task RunFetchAsync_CancelledToken_ReturnsCancelledWithoutSideEffects()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        host.RunCts = new CancellationTokenSource();
        await host.RunCts.CancelAsync();

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.Cancelled, outcome);
        Assert.AreEqual(0, host.State.DataVersion);
        Assert.AreEqual(0, host.RenderRequests);
    }

    // -- The inspector-refresh stamp ------------------------------------------

    [TestMethod]
    public async Task RunFetchAsync_CoordinateResolution_RequestsNoInspectorRefresh()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));

        var outcome = await host.Flow.RunFetchAsync();

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, outcome);
        Assert.AreEqual(0, host.Identity.Candidates.Count);
        Assert.AreEqual(0, host.InspectorRefreshes,
            "Coordinates produce no candidates: the empty stamp matches the empty baseline.");
    }

    [TestMethod]
    public async Task RunFetchAsync_SameCandidateSetTwice_RefreshesInspectorOnce()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        host.Location = BerlinCity;

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, await host.Flow.RunFetchAsync());
        Assert.AreEqual(1, host.Identity.Candidates.Count);
        Assert.AreEqual(1, host.InspectorRefreshes);

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, await host.Flow.RunFetchAsync(force: true));
        Assert.AreEqual(1, host.InspectorRefreshes,
            "An unchanged candidate stamp must not re-request the inspector.");
    }

    // -- CanFetch: the single cadence gate ------------------------------------

    [TestMethod]
    public async Task CanFetch_NonForcedCadence_HonorsTheClientThrottleWindow()
    {
        var clock = new FakeTimeProvider();
        var host = NewHost(new StubHttpHandler(FlowRespond), clock: clock);

        Assert.IsTrue(host.Flow.CanFetch(force: false), "A never-fetched client has an open window.");

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, await host.Flow.RunFetchAsync());

        Assert.IsFalse(host.Flow.CanFetch(force: false), "A fresh fetch stamp must cool the non-forced cadence.");
        Assert.IsTrue(host.Flow.CanFetch(force: true), "Force is always eligible.");

        clock.Advance(WeatherFetchControl.FetchWindow);
        Assert.IsTrue(host.Flow.CanFetch(force: false), "The elapsed window re-opens the non-forced cadence.");
    }

    [TestMethod]
    public async Task CanFetch_StaticSnapshot_VetoesNonForcedCadenceOnceTheFetchStampExists()
    {
        var clock = new FakeTimeProvider();
        var host = NewHost(new StubHttpHandler(FlowRespond), clock: clock);
        host.StaticSnapshotFlag = true;

        Assert.IsTrue(host.Flow.CanFetch(force: false),
            "Without a fetch stamp a static snapshot has nothing to protect — the window decides.");

        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, await host.Flow.RunFetchAsync());
        clock.Advance(WeatherFetchControl.FetchWindow);

        Assert.IsFalse(host.Flow.CanFetch(force: false),
            "A stamped static snapshot must veto the non-forced cadence even after the window elapses.");
        Assert.IsTrue(host.Flow.CanFetch(force: true));
    }

    // -- RunBootLoadAsync: the boot cache load --------------------------------

    [TestMethod]
    public async Task RunBootLoadAsync_MatchingIdentityAndVersion_AppliesCacheAtomically()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        var cached = new WeatherSnapshot(33.3, 30.0, 45, 10, 1, 35.0, 30.0, null, null, "Paris", 48.85, 2.35);
        host.Flow.CacheLoadOverride = (location, ct) => Task.FromResult<WeatherSnapshot?>(cached);

        await host.Flow.RunBootLoadAsync(CancellationToken.None);

        Assert.AreEqual(33.3, host.State.CurrentTempC);
        Assert.AreEqual(1, host.State.DataVersion);
        Assert.AreEqual("Paris", host.Identity.CityName);
        Assert.AreEqual(0, host.RenderRequests, "The boot load requests no render.");
    }

    [TestMethod]
    public async Task RunBootLoadAsync_FetchLandedDuringLoad_VersionGuardPreventsOverwrite()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        var cached = new WeatherSnapshot(99.9, null, null, null, null, null, null, null, null, "Paris", 48.85, 2.35);
        host.Flow.CacheLoadOverride = (location, ct) =>
        {
            // A fetch that completes while the load is in flight bumps the
            // data version (its apply ran under the same gate).
            lock (host.Gate)
            {
                host.State = host.State with { DataVersion = 1, CurrentTempC = 44.4 };
            }
            return Task.FromResult<WeatherSnapshot?>(cached);
        };

        await host.Flow.RunBootLoadAsync(CancellationToken.None);

        Assert.AreEqual(44.4, host.State.CurrentTempC,
            "A fetch that landed during the load must win; the stale cache must not overwrite it.");
        Assert.AreEqual(1, host.State.DataVersion, "The discarded cache must not bump the version.");
    }

    [TestMethod]
    public async Task RunBootLoadAsync_IdentityChangedDuringLoad_SkipsCacheAndRollsBackClientState()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        Assert.AreEqual(WeatherFetchFlowOutcome.Applied, await host.Flow.RunFetchAsync());
        Assert.IsFalse(host.Client.IsFetchWindowElapsed(), "The applied fetch stamps the throttle.");

        var cached = new WeatherSnapshot(99.9, null, null, null, null, null, null, null, null, "Paris", 48.85, 2.35);
        host.Flow.CacheLoadOverride = (location, ct) =>
        {
            // Profile hydration lands while the load is in flight: the
            // default-stamped cache must not surface under the hydrated
            // location (the identity guard's boot case).
            host.Location = BerlinCity;
            return Task.FromResult<WeatherSnapshot?>(cached);
        };

        await host.Flow.RunBootLoadAsync(CancellationToken.None);

        Assert.AreEqual(11.1, host.State.CurrentTempC,
            "A default-stamped cache must not surface under the hydrated location.");
        Assert.IsTrue(host.Client.IsFetchWindowElapsed(),
            "A discarded load must roll back the client's committed resolution state (the load's interface contract).");
        Assert.AreNotEqual("Paris", host.Identity.CityName);
    }

    [TestMethod]
    public async Task RunBootLoadAsync_MissingCache_HasNoSideEffects()
    {
        var stub = new StubHttpHandler(FlowRespond);
        var host = NewHost(stub);

        await host.Flow.RunBootLoadAsync(CancellationToken.None);

        Assert.AreEqual(0, host.State.DataVersion);
        Assert.AreEqual(25.0, host.State.CurrentTempC);
        Assert.IsNull(host.Identity.PendingWriteback);
        Assert.AreEqual(0, stub.Calls, "An empty cache directory makes no requests.");
    }

    [TestMethod]
    public async Task RunBootLoadAsync_CancelledToken_SwallowsTeardownCancellation()
    {
        var host = NewHost(new StubHttpHandler(FlowRespond));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        host.Flow.CacheLoadOverride = (location, token) => Task.FromCanceled<WeatherSnapshot?>(token);

        await host.Flow.RunBootLoadAsync(cts.Token); // must not throw

        Assert.AreEqual(0, host.State.DataVersion);
    }

    /// <summary>
    /// The test host: an adapter over the flow's host seam mirroring the
    /// widget's real seam wiring — the same apply policy under a gate, the
    /// same identity module, the same version-read and write-back discipline
    /// — so the flow sees exactly what the production host hands it. The
    /// mirror's fidelity is this adapter's discipline, not a copy of the
    /// wiring.
    /// </summary>
    private sealed class FlowHost : IWeatherFetchHost
    {
        public readonly Lock Gate = new();
        public WeatherSnapshotState State = new();
        public WeatherResolvedIdentity Identity = new("Default Location");
        public WeatherLocation Location = NycCoords;

        /// <summary>Optional override of the location read (the outcome-key
        /// mismatch test): when null, the read returns <see cref="Location"/>
        /// — one read per flow step, mirroring the widget's property
        /// coercion.</summary>
        public Func<WeatherLocation>? LocationSeam;

        public bool StaticSnapshotFlag { get; set; }
        public CancellationTokenSource? RunCts { get; set; }
        public int RenderRequests { get; set; }
        public int InspectorRefreshes { get; set; }
        public WeatherClient Client = null!;
        public WeatherFetchFlow Flow = null!;

        // -- IWeatherFetchHost: the seam the flow carries its host concerns across --

        /// <summary>The location read: the seam override when set, else the
        /// <see cref="Location"/> field.</summary>
        WeatherLocation IWeatherFetchHost.CurrentLocation => LocationSeam?.Invoke() ?? Location;

        /// <summary>The version read under the gate — mirrors the widget's
        /// gated read.</summary>
        int IWeatherFetchHost.DataVersion
        {
            get { lock (Gate) { return State.DataVersion; } }
        }

        bool IWeatherFetchHost.IsStaticSnapshot => StaticSnapshotFlag;
        CancellationToken IWeatherFetchHost.RunToken => RunCts?.Token ?? CancellationToken.None;
        void IWeatherFetchHost.RequestRender() => RenderRequests++;
        void IWeatherFetchHost.RequestInspectorRefresh() => InspectorRefreshes++;

        /// <summary>The gated apply — mirrors the widget's TryApply seam.</summary>
        bool IWeatherFetchHost.TryApply(WeatherApplyRequest request)
            => ApplySnapshot(request.Snapshot, request.ExpectedVersion, request.IdentityGuard,
                request.Candidates, request.Population, request.ResolvedName);

        /// <summary>The gated write-back queue: check + set under ONE lock —
        /// one critical section, mirroring the widget's seam.</summary>
        void IWeatherFetchHost.QueueLabelWriteback(Func<bool> identityGuard, string value)
        {
            lock (Gate)
            {
                if (identityGuard())
                {
                    Identity.SetPendingWriteback(value);
                }
            }
        }

        /// <summary>
        /// The gated apply: the policy's version-then-identity guard first,
        /// then the merge and the identity copies under ONE lock — the exact
        /// discipline the widget's TryApply seam spells (so the flow's
        /// guarantees are tested against the real gate shape).
        /// </summary>
        public bool ApplySnapshot(WeatherSnapshot snapshot, int? expectedVersion = null, Func<bool>? identityGuard = null,
            IReadOnlyList<GeocodeCandidate>? candidates = null, double? population = null, string? resolvedName = null)
        {
            lock (Gate)
            {
                if (!WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion, State.DataVersion, identityGuard)) return false;
                State = WeatherSnapshotApplyPolicy.Merge(snapshot, State);
                Identity.Apply(candidates, population, resolvedName);
                return true;
            }
        }
    }
}