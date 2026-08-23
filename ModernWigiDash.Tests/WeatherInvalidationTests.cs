namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the resolved-identity invalidation rule: the property →
/// drop-granularity map, the single drop rule over the shared identity value
/// (<see cref="WeatherInvalidation.Drop"/>), and the TWIN EQUIVALENCE — the
/// two twins (the client's fetch-control state machine and the widget's
/// gated display state) hold the SAME shared value type
/// (<see cref="WeatherResolutionState"/>) and route their drops through the
/// SAME rule, so after a drop their identities are structurally equal. The
/// unique field sets differ (the client owns the coordinates + identity query
/// + throttle, the widget owns the pending write-back); the RULE per kind is
/// what must agree, and this file is the pin that catches a twin that drifts
/// from the declared granularity.
/// </summary>
[TestClass]
public sealed class WeatherInvalidationTests
{
    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

    private static readonly WeatherSnapshot SeedSnapshot = new(
        23.4, 22.1, 65.0, 12.5, 2, 27.0, 18.0, null, null, "New York", 40.0, -74.0);

    private static GeocodeCandidate Candidate(string label, double population = 1000)
        => new(label, "Paris", 48.85, 2.35) { Population = population };

    private static WeatherResolutionState FullIdentity()
        => new("New York", 8_000_000.0, [Candidate("New York, New York, United States", 8_000_000)]);

    // ── The property → kind map ──────────────────────────────────────────

    [TestMethod]
    public void KindForProperty_LocationMatch_DropsCoordinatesOnly()
    {
        Assert.AreEqual(WeatherInvalidationKind.Coordinates, WeatherInvalidation.KindForProperty(WeatherQueryKey.LocationMatchProperty),
            "a pick keeps the candidates it was offered from");
    }

    [TestMethod]
    public void KindForProperty_EveryResolutionInput_DropsTheWholeIdentity()
    {
        foreach (string property in WeatherQueryKey.InvalidationProperties)
        {
            Assert.AreEqual(WeatherInvalidationKind.Location, WeatherInvalidation.KindForProperty(property),
                $"{property} is a resolution input — the whole identity must void");
        }
    }

    [TestMethod]
    public void KindForProperty_NonResolutionInput_DropsNothing()
    {
        // CustomLabel is deliberately identity-absent (ADR-0006): a label edit
        // must not re-fetch. The display properties are not resolution inputs.
        Assert.AreEqual(WeatherInvalidationKind.None, WeatherInvalidation.KindForProperty(nameof(WeatherLocation.CustomLabel)));
        Assert.AreEqual(WeatherInvalidationKind.None, WeatherInvalidation.KindForProperty("LayoutMode"));
        Assert.AreEqual(WeatherInvalidationKind.None, WeatherInvalidation.KindForProperty("AccentColorHex"));
    }

    // ── The single drop rule (pure — no twin involved) ────────────────────

    [TestMethod]
    public void Drop_None_KeepsTheIdentityAsIs()
    {
        var state = FullIdentity();

        Assert.AreSame(state, WeatherInvalidation.Drop(WeatherInvalidationKind.None, state),
            "None must not touch the identity value");
    }

    [TestMethod]
    public void Drop_Cordinates_DropsNameAndPopulationButKeepsTheOfferedCandidates()
    {
        var state = FullIdentity();

        var dropped = WeatherInvalidation.Drop(WeatherInvalidationKind.Coordinates, state);

        Assert.AreEqual("", dropped.ResolvedName, "the pick voids the old winner's name");
        Assert.AreEqual(0.0, dropped.Population, "the pick voids the old winner's population");
        Assert.AreSame(state.Candidates, dropped.Candidates,
            "the pick's candidates must survive the coordinates drop");
    }

    [TestMethod]
    public void Drop_Location_EmptiesTheWholeIdentityIntoTheOneEmptyState()
    {
        var state = FullIdentity();

        var dropped = WeatherInvalidation.Drop(WeatherInvalidationKind.Location, state);

        Assert.AreSame(WeatherResolutionState.Empty, dropped,
            "the whole identity must void to the one empty state, so both twins can land on the same instance");
    }

    // ── Twin equivalence: both twins route through the same rule ──────────

    /// <summary>Seeds BOTH twins from the SAME shared identity: one
    /// candidates-list instance, the same name and population, a stamped
    /// throttle on the client, a pending label on the widget. Returning the
    /// client's seeded state as the seed keeps the rule's input identical to
    /// what the twins actually hold.</summary>
    private static (WeatherFetchControl Control, WeatherDisplayState State, WeatherResolutionState Seed) SeedBoth()
    {
        IReadOnlyList<GeocodeCandidate> sharedCandidates = [Candidate("New York, New York, United States", 8_000_000)];
        var control = new WeatherFetchControl(FixedClock());
        control.AdvanceResolution("key");
        control.SetResolved(40.0, -74.0, "New York", 8_000_000);
        control.SetCandidates(sharedCandidates);
        Assert.IsTrue(control.Stamp("key"), "the seed must stamp the throttle");
        var state = new WeatherDisplayState(WeatherPresentation.UnknownLocationLabel, () => DateTime.UtcNow);
        state.TryApply(new WeatherApplyRequest(SeedSnapshot, null, null, sharedCandidates, 8_000_000.0, "New York"));
        state.QueueLabelWriteback(() => true, "New York");
        return (control, state, control.ResolutionState);
    }

    [TestMethod]
    public void CoordinatesKind_BothTwins_DropThroughTheRuleAndKeepTheCandidates()
    {
        var (control, state, seed) = SeedBoth();

        // The client twin's Coordinates drop (the widget pairs the same kind
        // through its own gated entry).
        control.Invalidate(WeatherInvalidationKind.Coordinates);
        // The widget twin's Coordinates drop.
        state.Invalidate(WeatherInvalidationKind.Coordinates);

        // THE DECLARED RULE: both twins' post-drop identity IS the rule's
        // output for the shared seed — equal to it, and equal across twins.
        var dropped = WeatherInvalidation.Drop(WeatherInvalidationKind.Coordinates, seed);
        Assert.AreEqual(dropped, control.ResolutionState,
            "the client twin must route the identity through the single drop rule");
        Assert.AreEqual(dropped, state.Identity,
            "the widget twin must route the identity through the single drop rule");
        Assert.AreEqual(control.ResolutionState, state.Identity,
            "twin equivalence: after the same drop, the same identity value on both");
        // THE DECLARED RULE: the candidates survive on BOTH twins — a pick
        // resolves against the candidates it was offered from.
        Assert.AreEqual(1, control.ResolutionState.Candidates.Count,
            "the pick's candidates must survive the coordinates drop");
        Assert.AreEqual(1, state.Identity.Candidates.Count,
            "the pick's candidates must survive the coordinates drop");
        // The widget twin's unique field: the pending write-back drops with
        // the resolution.
        Assert.IsNull(state.TakePendingWriteback(new WeatherLocation("Fixed Location", "", null, null, null), () => false),
            "a pending label must not survive the pick that voids it");
        // The client twin's unique fields: the coordinates clear, and the
        // identity query + throttle reset so the pick re-resolves immediately.
        Assert.IsNull(control.Lat);
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.LastLocationQuery);
        Assert.AreEqual(DateTime.MinValue, control.LastFetchTimeUtc);
    }

    [TestMethod]
    public void LocationKind_BothTwins_VoidTheWholeIdentity()
    {
        var (control, state, _) = SeedBoth();

        // The client twin's Location drop: one kind through the one entry —
        // the coordinates drop plus the whole-identity void (candidates
        // included), no two-step spelling.
        control.Invalidate(WeatherInvalidationKind.Location);
        // The widget twin's Location drop.
        state.Invalidate(WeatherInvalidationKind.Location);

        // THE DECLARED RULE: the WHOLE resolved identity voids on both twins —
        // nothing of the old resolution may survive. Compared field by field:
        // the two empty states carry fresh empty candidate lists, and the
        // record's default equality on the list is reference-based — the pure
        // Drop pin above is the structural Location pin (AreSame(Empty)).
        Assert.AreEqual("", control.ResolutionState.ResolvedName);
        Assert.AreEqual(0.0, control.ResolutionState.Population);
        Assert.AreEqual(0, control.ResolutionState.Candidates.Count,
            "a stale pick must never win against a new input");
        Assert.AreEqual("", state.Identity.ResolvedName);
        Assert.AreEqual(0.0, state.Identity.Population);
        Assert.AreEqual(0, state.Identity.Candidates.Count,
            "a stale pick must never win against a new input");
        // The widget twin's unique field.
        Assert.IsNull(state.TakePendingWriteback(new WeatherLocation("Fixed Location", "", null, null, null), () => false));
        // The client twin's unique fields.
        Assert.IsNull(control.Lat);
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.LastLocationQuery);
        Assert.AreEqual(DateTime.MinValue, control.LastFetchTimeUtc);
    }

    [TestMethod]
    public void NoneKind_BothTwins_KeepEveryResolvedField()
    {
        var (control, state, _) = SeedBoth();

        // No kind → no twin operation runs; the shared identity value must be
        // untouched on both, and structurally equal across twins (the seeded
        // identity works on the shared value type).
        Assert.AreEqual(WeatherInvalidation.Drop(WeatherInvalidationKind.None, control.ResolutionState), control.ResolutionState,
            "the None rule is the identity function on the shared value");
        Assert.AreEqual(WeatherInvalidation.Drop(WeatherInvalidationKind.None, state.Identity), state.Identity,
            "the None rule is the identity function on the shared value");
        Assert.AreEqual(control.ResolutionState, state.Identity,
            "the seeded identity is structurally equal on both twins");
        // The unique fields are untouched, too.
        Assert.AreEqual(40.0, control.Lat);
        Assert.AreEqual("New York", state.Identity.ResolvedName);
        Assert.AreEqual("New York", state.TakePendingWriteback(
                    new WeatherLocation("Fixed Location", "", null, null, null), () => false),
                    "the pending write-back must survive a non-resolution edit");
    }
}
