namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the weather widget's gated display-state module: the apply and the
/// tie apply under the one gate (guard → merge → identity copies → stamp as
/// one critical section), the write-back queue/take serialization (a queued
/// value can never be lost to a take), the folded resolved-identity value's
/// apply rules (the null-keeps and the population's no-data sentinel), the
/// invalidation routing, and the render tick's version-gated consistent view
/// — drivable without a widget instance, a fetch, or an HTTP stub.
/// </summary>
[TestClass]
public class WeatherDisplayStateTests
{
    private static readonly DateTime Stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private static readonly IReadOnlyList<GeocodeCandidate> Candidates =
    [
        new GeocodeCandidate("Victoria, British Columbia, Canada", "Victoria, British Columbia, Canada", 48.4284, -123.3656),
        new GeocodeCandidate("Victoria, Seychelles", "Victoria, Seychelles", -9.4321, 55.4654),
    ];

    private static readonly WeatherSnapshot FullSnapshot = new(
        CurrentTempC: 23.4, FeelsLikeC: 22.1, Humidity: 65.0, WindSpeedKmH: 12.5,
        WeatherCode: 2, HighTempC: 27.0, LowTempC: 18.0,
        DailyForecasts: [new("Mon", 27.0, 18.0, 2), new("Tue", 28.0, 19.0, 61)],
        HourlyForecasts: [new("12:00", 24.0, 3), new("13:00", 25.0, 3)],
        ResolvedCityName: "Miami, Florida, United States of America", Lat: 25.76, Lon: -80.19);

    private static WeatherDisplayState NewState(Func<DateTime>? now = null)
        => new("Default Location", now ?? (() => Stamp));

    private static WeatherApplyRequest ApplyRequest(WeatherSnapshot snapshot, int? expectedVersion = null,
        Func<bool>? identityGuard = null, IReadOnlyList<GeocodeCandidate>? candidates = null,
        double? population = null, string? resolvedName = null)
        => new(snapshot, expectedVersion, identityGuard, candidates, population, resolvedName);

    // -- TryApply -------------------------------------------------------------

    [TestMethod]
    public void TryApply_Fresh_SucceedsMergesIdentityAndStampsLastSuccess()
    {
        var state = NewState();

        bool applied = state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates, population: 444_000.0,
            resolvedName: "Miami, Florida, United States of America"));

        Assert.IsTrue(applied);
        Assert.AreEqual(1, state.DataVersion);
        Assert.IsTrue(state.State.HasData, "the apply is a snapshot's commit — from here the pane renders the data, not its no-data view");
        Assert.AreEqual(23.4, state.State.CurrentTempC);
        Assert.AreEqual("Miami, Florida, United States of America", state.Identity.ResolvedName);
        Assert.AreEqual(444_000.0, state.Identity.Population);
        Assert.AreEqual(2, state.Identity.Candidates.Count);
        Assert.AreEqual(Stamp, state.LastSuccessFetchTime, "the stamp rides the apply's critical section");
    }

    [TestMethod]
    public void TryApply_StaleExpectedVersion_RejectsLeavesStateAndStampUntouched()
    {
        int tick = 0;
        var state = NewState(() => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(tick++));
        state.TryApply(ApplyRequest(FullSnapshot));

        bool applied = state.TryApply(ApplyRequest(FullSnapshot, expectedVersion: 0));

        Assert.IsFalse(applied);
        Assert.AreEqual(1, state.DataVersion, "the stale apply must not bump the version");
        Assert.AreEqual(23.4, state.State.CurrentTempC);
        Assert.AreEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), state.LastSuccessFetchTime,
            "a rejected apply must not re-stamp the last-success time");
    }

    [TestMethod]
    public void TryApply_IdentityGuardFails_RejectsLeavesIdentityUntouched()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates,
            resolvedName: "Miami, Florida, United States of America"));

        bool applied = state.TryApply(ApplyRequest(FullSnapshot, expectedVersion: 1, identityGuard: () => false,
            resolvedName: "Berlin, Germany"));

        Assert.IsFalse(applied);
        Assert.AreEqual("Miami, Florida, United States of America", state.Identity.ResolvedName,
            "an edit that changed the resolution inputs since the fetch wins over the stale apply");
        Assert.AreEqual(1, state.DataVersion);
    }

    // -- TryApplyTie ----------------------------------------------------------

    [TestMethod]
    public void TryApplyTie_GuardFails_LeavesStateAndIdentityUntouched()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, resolvedName: "Miami, Florida, United States of America"));

        bool applied = state.TryApplyTie(Candidates, () => false, () => "Berlin");

        Assert.IsFalse(applied);
        Assert.AreEqual(1, state.DataVersion, "the rejected tie must not bump the version");
        Assert.AreEqual(23.4, state.State.CurrentTempC, "the previous city's scalars stay");
        Assert.AreEqual(0, state.Identity.Candidates.Count);
    }

    [TestMethod]
    public void TryApplyTie_Success_ResetsToPlaceholderAppliesTiedCandidatesAndQueriedHeader()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates, population: 444_000.0,
            resolvedName: "Miami, Florida, United States of America"));

        bool applied = state.TryApplyTie(
            [new GeocodeCandidate("Berlin, Germany", "Berlin, Germany", 52.52, 13.405)],
            () => true, () => "Berlin");

        Assert.IsTrue(applied);
        Assert.AreEqual(2, state.DataVersion, "the placeholder reset bumps the version so the render model rebuilds");
        Assert.IsFalse(state.State.HasData,
            "a tie has no data — the reset lands on the no-data view, never a previous city's scalars under the tie's header");
        Assert.AreEqual(new WeatherSnapshotState().CurrentTempC, state.State.CurrentTempC,
            "a tie has no data — the placeholder scalar (the record's default), never a previous city's");
        Assert.AreEqual(1, state.Identity.Candidates.Count);
        Assert.AreEqual("Berlin, Germany", state.Identity.Candidates[0].Label);
        Assert.AreEqual("Berlin", state.Identity.ResolvedName, "the queried name is the honest header — there is no winner to name");
        Assert.AreEqual(0, state.Identity.Population);
    }

    [TestMethod]
    public void TryApplyTie_BlankQueriedLocation_HeaderIsNeutralLabel()
    {
        var state = NewState();

        bool applied = state.TryApplyTie(Candidates, () => true, () => "  ");

        Assert.IsTrue(applied);
        Assert.AreEqual("Default Location", state.Identity.ResolvedName,
                    "a blank query has no name to show — the INJECTED neutral label (the seam is parameterized, not a hardcoded client const)");
    }

    // -- Write-back queue / take ----------------------------------------------

    private static WeatherLocation BareLocation(string location = "")
        => new("Fixed Location", location, null, null, null);

    [TestMethod]
    public void QueueLabelWriteback_GuardFails_LeavesQueueEmpty()
    {
        var state = NewState();

        state.QueueLabelWriteback(() => false, "Amsterdam, Netherlands");

        Assert.IsNull(state.PendingLabelWriteback);
        Assert.IsNull(state.TakePendingWriteback(BareLocation(), () => false),
            "a never-queued write-back takes back null — not an empty string");
    }

    [TestMethod]
    public void TakePendingWriteback_ReturnsAndClears_EveryQueuedValueSurvivesItsOwnTake()
    {
        var state = NewState();
        state.QueueLabelWriteback(() => true, "First");

        string? first = state.TakePendingWriteback(BareLocation(), () => false);
        state.QueueLabelWriteback(() => true, "Second");

        string? second = state.TakePendingWriteback(BareLocation(), () => false);

        Assert.AreEqual("First", first);
        Assert.AreEqual("Second", second,
            "the queue and the take serialize on the one gate — a queued value can never be lost to a take");
        Assert.IsNull(state.PendingLabelWriteback);
    }

    [TestMethod]
    public void TakePendingWriteback_CustomLabelSetAfterTheQueue_RefusesAndKeepsQueued()
    {
        // The gap the old ungated flush check sailed through: a CustomLabel
        // landing between the queue and the flush must veto the write at the
        // take — and a veto is a "not yet", never a "never" (the value stays
        // queued; removing the label lets the next take through).
        var state = NewState();
        state.QueueLabelWriteback(() => true, "Miami, Florida, United States of America");

        string? taken = state.TakePendingWriteback(BareLocation() with { CustomLabel = "Home" }, () => false);

        Assert.IsNull(taken, "a CustomLabel set after the queue must veto the write at the take");
        Assert.AreEqual("Miami, Florida, United States of America", state.PendingLabelWriteback,
            "the vetoed write-back stays queued — a veto must never silently lose the resolved label");
        Assert.AreEqual("Miami, Florida, United States of America",
            state.TakePendingWriteback(BareLocation(), () => false),
            "removing the label re-opens the take on the next frame");
        Assert.IsNull(state.PendingLabelWriteback);
    }

    [TestMethod]
    public void TakePendingWriteback_NameEqualsLocation_RefusesAndKeepsQueued()
    {
        var state = NewState();
        state.QueueLabelWriteback(() => true, "Berlin");

        string? taken = state.TakePendingWriteback(BareLocation("Berlin"), () => false);

        Assert.IsNull(taken, "writing the location onto itself is a no-op churn — the take refuses");
        Assert.AreEqual("Berlin", state.PendingLabelWriteback, "the refused write-back stays queued");
    }

    [TestMethod]
    public void TakePendingWriteback_Suppressed_RefusesAndKeepsQueued()
    {
        var state = NewState();
        state.QueueLabelWriteback(() => true, "Berlin");

        string? taken = state.TakePendingWriteback(BareLocation(), () => true);

        Assert.IsNull(taken, "the suppression flag's veto runs at the take, under the gate");
        Assert.AreEqual("Berlin", state.PendingLabelWriteback);
    }

    [TestMethod]
    public void WritebackEligible_SpellsTheThreeConditionsOnce()
    {
        var bare = BareLocation("New York");

        Assert.IsTrue(WeatherDisplayState.WritebackEligible("New York, New York, United States", bare),
            "a non-empty name with no CustomLabel and a differing Location is eligible");
        Assert.IsFalse(WeatherDisplayState.WritebackEligible("", bare), "a blank name has nothing to write");
        Assert.IsFalse(WeatherDisplayState.WritebackEligible("   ", bare), "whitespace-only is blank");
        Assert.IsFalse(WeatherDisplayState.WritebackEligible("New York, New York, United States",
                BareLocation("New York") with { CustomLabel = "Home" }),
            "a CustomLabel claims the title — the label is display-only");
        Assert.IsFalse(WeatherDisplayState.WritebackEligible("New York", bare),
            "a name that equals the Location is a no-op write");
    }

    // -- Invalidation routing ---------------------------------------------------

    [TestMethod]
    public void Invalidate_Coordinates_DropsNamePopulationAndWriteback_KeepsCandidates()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates, population: 444_000.0,
            resolvedName: "Miami, Florida, United States of America"));
        state.QueueLabelWriteback(() => true, "Miami, Florida, United States of America");

        state.Invalidate(WeatherInvalidationKind.Coordinates);

        Assert.AreEqual("", state.Identity.ResolvedName);
        Assert.AreEqual(0, state.Identity.Population);
        Assert.AreEqual(2, state.Identity.Candidates.Count,
            "a Location Match pick resolves against the candidates it was offered from");
        Assert.IsNull(state.PendingLabelWriteback);
    }

    [TestMethod]
    public void Invalidate_Location_DropsWholeIdentityIncludingCandidates()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates, population: 444_000.0,
            resolvedName: "Miami, Florida, United States of America"));
        state.QueueLabelWriteback(() => true, "Miami, Florida, United States of America");

        state.Invalidate(WeatherInvalidationKind.Location);

        Assert.AreEqual(0, state.Identity.Candidates.Count);
        Assert.AreEqual(0, state.Identity.Population);
        Assert.AreEqual("", state.Identity.ResolvedName);
        Assert.IsNull(state.PendingLabelWriteback);
    }

    // -- The folded resolved identity (value + null-keeps + sentinel) -------
    // These pins moved here when the WeatherResolvedIdentity module was
    // folded into the display state: the identity value's transitions now run
    // only through the gated members, so the pins ride TryApply.

    [TestMethod]
    public void Ctor_NeutralLabel_IsTheInitialHeader()
    {
        var state = NewState();

        Assert.AreEqual("Default Location", state.Identity.ResolvedName,
            "the header must show the neutral label until a resolution sets a real identity");
        Assert.AreEqual(0, state.Identity.Candidates.Count);
        Assert.AreEqual(0, state.Identity.Population);
        Assert.IsNull(state.PendingLabelWriteback);
    }

    [TestMethod]
    public void Apply_NullArguments_KeepPreviousValues()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates, population: 444_000.0,
            resolvedName: "Miami, Florida, United States of America"));

        // The fetch reported none of the three identity sections: nothing may
        // change (the "response omitted this section — keep the previous
        // value" rule).
        state.TryApply(ApplyRequest(FullSnapshot, expectedVersion: 1));

        Assert.AreEqual("Miami, Florida, United States of America", state.Identity.ResolvedName);
        Assert.AreEqual(444_000.0, state.Identity.Population);
        Assert.AreEqual(2, state.Identity.Candidates.Count);
    }

    [TestMethod]
    public void Apply_ZeroPopulation_ClearsTheResolvedPopulation()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot, candidates: Candidates, population: 444_000.0,
            resolvedName: "Miami, Florida, United States of America"));

        // The client's no-data sentinel: 0 clears, null keeps.
        state.TryApply(ApplyRequest(FullSnapshot, expectedVersion: 1, population: 0));

        Assert.AreEqual(0, state.Identity.Population);
        Assert.AreEqual("Miami, Florida, United States of America", state.Identity.ResolvedName,
            "only the population clears; the name and candidates are untouched");
    }

    // -- The render tick's consistent view --------------------------------------

    private static readonly SKRect Bounds = new(0, 0, 1016, 592);
    private static readonly WeatherHeaderLayout Header = WeatherLayout.ComputeHeader(Bounds, 1f, 1f);

    private static (WeatherRenderModelInputs, DateTime) View(WeatherDisplayState state, bool hideLocation = false)
            => state.CaptureRenderView(Bounds, Header, 1f, "Detailed", "Fahrenheit (°F, mph)", "", hideLocation,
                true, true, true, true, true, "Miami, Florida"); // all five display options on

    [TestMethod]
    public void CaptureRenderView_VersionUnchanged_CopiesAreStableAcrossCaptures()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot));

        var (v1, t1) = View(state);
        var (v2, _) = View(state);

        Assert.AreSame(v1.Daily, v2.Daily,
            "the forecast copies refresh only when the version changes — the per-frame path allocates nothing");
        Assert.AreSame(v1.Hourly, v2.Hourly);
        Assert.AreEqual(2, v1.Daily.Count);
        Assert.AreEqual(2, v1.Hourly.Count);
        Assert.AreEqual(1, v1.Key.DataVersion);
        Assert.AreEqual(23.4, v1.CurrentTempC);
        Assert.AreEqual("Miami, Florida", v1.LocationText);
        Assert.AreEqual(Stamp, t1);
    }

    [TestMethod]
    public void CaptureRenderView_HideLocation_RidesTheKey()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot));

        var (v1, _) = View(state);
        var (v2, _) = View(state, hideLocation: true);

        Assert.IsFalse(v1.Key.HideLocation);
        Assert.IsTrue(v2.Key.HideLocation,
            "the hide-location flag must ride the render-model key — the header title is a key-owned display fact");
    }

    [TestMethod]
    public void CaptureRenderView_TheDataFact_RidesTheKey()
    {
        var state = NewState();

        var (before, _) = View(state);
        Assert.IsFalse(before.Key.HasData,
            "a fresh state has no committed snapshot — the no-data view is what the pane draws");

        state.TryApply(ApplyRequest(FullSnapshot));
        var (after, _) = View(state);
        Assert.IsTrue(after.Key.HasData,
            "the apply's commit must ride the render-model key — the pane switches to the data view");
    }

    [TestMethod]
    public void CaptureRenderView_TieThenReapplyWithoutCapture_RefreshesCopiesNeverThePreviousCitysList()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot));
        var (v1, _) = View(state);

        // A tie and a re-apply with NO capture in between: the placeholder
        // reset must not reset the forecast version onto the already-rendered
        // one — a version collision would make the copy-skip reuse the
        // previous city's list under the new city's header.
        state.TryApplyTie(
            [new GeocodeCandidate("Berlin, Germany", "Berlin, Germany", 52.52, 13.405)],
            () => true, () => "Berlin");
        state.TryApply(ApplyRequest(new WeatherSnapshot(
            CurrentTempC: 30.0, FeelsLikeC: 29.0, Humidity: 40.0, WindSpeedKmH: 8.0,
            WeatherCode: 95, HighTempC: 33.0, LowTempC: 24.0,
            DailyForecasts: [new("Wed", 33.0, 24.0, 95)],
            HourlyForecasts: [new("14:00", 30.0, 95)],
            ResolvedCityName: "Phoenix, Arizona, United States of America", Lat: 33.45, Lon: -112.07)));
        var (v2, _) = View(state);

        Assert.AreNotSame(v1.Daily, v2.Daily,
            "the re-apply after a tie must refresh the copies — the placeholder reset must not reset the forecast version onto a previously rendered one");
        Assert.AreEqual(1, v2.Daily.Count, "the new city's forecast list, never the previous city's stale copy");
        Assert.AreEqual(1, v2.Hourly.Count);
    }

    [TestMethod]
    public void CaptureRenderView_AfterTie_RefreshesCopiesToTheEmptyPlaceholder()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot));

        state.TryApplyTie(
            [new GeocodeCandidate("Berlin, Germany", "Berlin, Germany", 52.52, 13.405)],
            () => true, () => "Berlin");
        var (v1, _) = View(state);

        Assert.AreEqual(0, v1.Daily.Count, "a tie has no forecast data — the placeholder's empty lists, never a previous city's");
        Assert.AreEqual(0, v1.Hourly.Count);
    }

    [TestMethod]
    public void CaptureRenderView_VersionChanged_RefreshesCopies()
    {
        var state = NewState();
        state.TryApply(ApplyRequest(FullSnapshot));
        var (v1, _) = View(state);

        state.TryApply(ApplyRequest(new WeatherSnapshot(
            CurrentTempC: 30.0, FeelsLikeC: 29.0, Humidity: 40.0, WindSpeedKmH: 8.0,
            WeatherCode: 95, HighTempC: 33.0, LowTempC: 24.0,
            DailyForecasts: [new("Wed", 33.0, 24.0, 95)],
            HourlyForecasts: [new("14:00", 30.0, 95)],
            ResolvedCityName: "Phoenix, Arizona, United States of America", Lat: 33.45, Lon: -112.07)));
        var (v2, _) = View(state);

        Assert.AreNotSame(v1.Daily, v2.Daily);
        Assert.AreEqual(1, v2.Daily.Count);
        Assert.AreEqual(2, v2.Key.DataVersion);
        Assert.AreEqual(30.0, v2.CurrentTempC);
    }
}
