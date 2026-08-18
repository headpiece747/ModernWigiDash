using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the resolved-identity invalidation rule: the property →
/// drop-granularity map, and the TWIN EQUIVALENCE — the two twins (the
/// client's fetch-control state machine and the widget's resolved identity)
/// drop and keep EXACTLY the declared granularity for each kind. The field
/// sets differ (the client owns throttle + identity query, the widget owns
/// the pending write-back); the RULE per kind is what must agree, and this
/// file is the pin that catches a twin that drifts from the declared
/// granularity.
/// </summary>
[TestClass]
public sealed class WeatherInvalidationTests
{
    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

    private static GeocodeCandidate Candidate(string label, double population = 1000)
        => new(label, "Paris", 48.85, 2.35) { Population = population };

    /// <summary>Seeds a control with the FULL resolved identity: coordinates,
    /// name, population, candidates, and a stamped throttle.</summary>
    private static void SeedControl(WeatherFetchControl control)
    {
        control.AdvanceResolution("key");
        control.SetResolved(40.0, -74.0, "New York", 8_000_000);
        control.SetCandidates([Candidate("New York, New York, United States", 8_000_000)]);
        Assert.IsTrue(control.Stamp("key"), "the seed must stamp the throttle");
    }

    /// <summary>Seeds the identity twin with the FULL resolved identity:
    /// name, population, candidates, and a pending label write-back.</summary>
    private static void SeedIdentity(WeatherResolvedIdentity identity)
    {
        identity.Apply(candidates: [Candidate("New York, New York, United States", 8_000_000)], population: 8_000_000.0, resolvedName: "New York");
        identity.SetPendingWriteback("New York");
    }

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

    // ── Twin equivalence: the declared granularity, per kind ─────────────

    [TestMethod]
    public void CoordinatesKind_BothTwins_DropTheResolutionAndKeepTheCandidates()
    {
        var control = new WeatherFetchControl(FixedClock());
        SeedControl(control);
        var identity = new WeatherResolvedIdentity(WeatherFetchControl.UnknownLocationLabel);
        SeedIdentity(identity);

        // The client twin's Coordinates drop (the widget pairs it under the gate).
        control.Invalidate();
        // The widget twin's Coordinates drop.
        identity.InvalidateCoordinates();

        // Coordinates, name, and population drop on both twins...
        Assert.IsNull(control.Lat);
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.ResolvedCityName);
        Assert.AreEqual(0, control.ResolvedPopulation);
        Assert.AreEqual("", identity.CityName);
        Assert.AreEqual(0, identity.Population);
        // ...and the widget's pending write-back drops with the resolution.
        Assert.IsNull(identity.TakePendingWriteback(), "a pending label must not survive the pick that voids it");
        // THE DECLARED RULE: the candidates survive on BOTH twins — a pick
        // resolves against the candidates it was offered from.
        Assert.AreEqual(1, control.Candidates.Count, "the pick's candidates must survive the coordinates drop");
        Assert.AreEqual(1, identity.Candidates.Count, "the pick's candidates must survive the coordinates drop");
        // The client twin resets the identity query + throttle so the pick
        // re-resolves immediately.
        Assert.AreEqual("", control.LastLocationQuery);
        Assert.AreEqual(DateTime.MinValue, control.LastFetchTimeUtc);
    }

    [TestMethod]
    public void LocationKind_BothTwins_VoidTheWholeIdentity()
    {
        var control = new WeatherFetchControl(FixedClock());
        SeedControl(control);
        var identity = new WeatherResolvedIdentity(WeatherFetchControl.UnknownLocationLabel);
        SeedIdentity(identity);

        // The client twin's Location drop = coordinates drop + candidates drop.
        control.Invalidate();
        control.ClearCandidates();
        // The widget twin's Location drop.
        identity.InvalidateLocation();

        // THE DECLARED RULE: the WHOLE resolved identity voids on both twins —
        // nothing of the old resolution may survive.
        Assert.IsNull(control.Lat);
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.ResolvedCityName);
        Assert.AreEqual(0, control.ResolvedPopulation);
        Assert.AreEqual(0, control.Candidates.Count, "a stale pick must never win against a new input");
        Assert.AreEqual("", identity.CityName);
        Assert.AreEqual(0, identity.Population);
        Assert.AreEqual(0, identity.Candidates.Count, "a stale pick must never win against a new input");
        Assert.IsNull(identity.TakePendingWriteback());
        Assert.AreEqual("", control.LastLocationQuery);
        Assert.AreEqual(DateTime.MinValue, control.LastFetchTimeUtc);
    }

    [TestMethod]
    public void NoneKind_BothTwins_KeepEveryResolvedField()
    {
        var control = new WeatherFetchControl(FixedClock());
        SeedControl(control);
        var identity = new WeatherResolvedIdentity(WeatherFetchControl.UnknownLocationLabel);
        SeedIdentity(identity);

        // No kind → no twin operation runs; the state must be untouched.
        Assert.AreEqual(40.0, control.Lat);
        Assert.AreEqual("New York", identity.CityName);
        Assert.AreEqual(1, control.Candidates.Count);
        Assert.AreEqual("New York", identity.TakePendingWriteback(), "the pending write-back must survive a non-resolution edit");
    }
}