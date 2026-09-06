namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the resolved-identity invalidation rule: the property →
/// drop-granularity map and the single drop rule over the shared identity
/// value (<see cref="WeatherInvalidation.Drop"/>). The former twin-equivalence
/// ceremony is gone by construction: the client's fetch-side fields and the
/// widget's display-side state share ONE identity owner
/// (<see cref="WeatherResolution"/>), so there is one storage, one gate, and
/// one application site for a drop — nothing to drift.
/// </summary>
[TestClass]
public sealed class WeatherInvalidationTests
{
    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

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

    // ── The single drop rule (pure — no module involved) ───────────────────

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
            "the whole identity must void to the one empty state");
    }

    // ── One owner: the drop applies to the shared identity under one gate ──

    /// <summary>Seeds the ONE identity owner from a full resolution: the
    /// shared identity value, the fetch-side coordinates + query + throttle,
    /// and the display-side pending write-back.</summary>
    private static (WeatherResolution Resolution, WeatherDisplayState State) SeedOwner()
    {
        IReadOnlyList<GeocodeCandidate> candidates = [Candidate("New York, New York, United States", 8_000_000)];
        var clock = FixedClock();
        var resolution = new WeatherResolution(clock, WeatherPresentation.UnknownLocationLabel);
        resolution.AdvanceResolution("key");
        resolution.SetResolved(40.0, -74.0, "New York", 8_000_000);
        resolution.SetCandidates(candidates);
        Assert.IsTrue(resolution.Stamp("key"), "the seed must stamp the throttle");
        var state = new WeatherDisplayState(resolution, WeatherPresentation.UnknownLocationLabel, () => DateTime.UtcNow);
        state.Resolution.QueueLabelWriteback(() => true, "New York");
        return (resolution, state);
    }

    [TestMethod]
    public void CoordinatesKind_DropsThroughTheRuleAndKeepsTheCandidates()
    {
        var (resolution, state) = SeedOwner();
        WeatherResolutionState seed = resolution.Identity;

        // The widget's gated entry routes through the owner's one application
        // site (the client's Invalidate forwards to the same member).
        state.Invalidate(WeatherInvalidationKind.Coordinates);

        // THE DECLARED RULE: the post-drop identity IS the rule's output for
        // the seeded value.
        var dropped = WeatherInvalidation.Drop(WeatherInvalidationKind.Coordinates, seed);
        Assert.AreEqual(dropped, resolution.Identity,
            "the owner must route the identity through the single drop rule");
        // THE DECLARED RULE: the candidates survive — a pick resolves against
        // the candidates it was offered from.
        Assert.AreEqual(1, resolution.Identity.Candidates.Count,
            "the pick's candidates must survive the coordinates drop");
        // The display-side unique field: the pending write-back drops with
        // the resolution.
        Assert.IsNull(state.Resolution.TakePendingWriteback(new WeatherLocation("Fixed Location", "", null, null, null), () => false),
            "a pending label must not survive the pick that voids it");
        // The fetch-side unique fields: the coordinates clear, and the
        // identity query + throttle reset so the pick re-resolves immediately.
        Assert.IsNull(resolution.Lat);
        Assert.IsNull(resolution.Lon);
        Assert.AreEqual("", resolution.LastLocationQuery);
        Assert.AreEqual(DateTime.MinValue, resolution.LastFetchTimeUtc);
    }

    [TestMethod]
    public void LocationKind_VoidsTheWholeIdentity()
    {
        var (resolution, state) = SeedOwner();

        state.Invalidate(WeatherInvalidationKind.Location);

        // THE DECLARED RULE: the WHOLE resolved identity voids — nothing of
        // the old resolution may survive.
        Assert.AreEqual("", resolution.Identity.ResolvedName);
        Assert.AreEqual(0.0, resolution.Identity.Population);
        Assert.AreEqual(0, resolution.Identity.Candidates.Count,
            "a stale pick must never win against a new input");
        // The display-side unique field.
        Assert.IsNull(state.Resolution.TakePendingWriteback(new WeatherLocation("Fixed Location", "", null, null, null), () => false));
        // The fetch-side unique fields.
        Assert.IsNull(resolution.Lat);
        Assert.IsNull(resolution.Lon);
        Assert.AreEqual("", resolution.LastLocationQuery);
        Assert.AreEqual(DateTime.MinValue, resolution.LastFetchTimeUtc);
    }

    [TestMethod]
    public void NoneKind_KeepEveryResolvedField()
    {
        var (resolution, state) = SeedOwner();

        // No kind → no operation runs; the shared identity value must be
        // untouched, and the None rule is the identity function on it.
        Assert.AreEqual(WeatherInvalidation.Drop(WeatherInvalidationKind.None, resolution.Identity), resolution.Identity,
            "the None rule is the identity function on the shared value");
        // The unique fields are untouched, too.
        Assert.AreEqual(40.0, resolution.Lat);
        Assert.AreEqual("New York", resolution.Identity.ResolvedName);
        Assert.AreEqual("New York", state.Resolution.TakePendingWriteback(
                    new WeatherLocation("Fixed Location", "", null, null, null), () => false),
                    "the pending write-back must survive a non-resolution edit");
    }
}
