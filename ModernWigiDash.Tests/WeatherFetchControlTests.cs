using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherFetchControlTests
{
    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

    private static WeatherFetchControl CreateControl(out FakeTimeProvider clock)
    {
        clock = FixedClock();
        return new WeatherFetchControl(clock);
    }

    private static GeocodeCandidate Candidate(string label, double population = 1000)
        => new(label, "Paris", 48.85, 2.35) { Population = population };

    // ── Begin / single-flight claim / throttle ──────────────────────────

    [TestMethod]
    public void Begin_FreshControl_ReturnsStarted()
    {
        var control = CreateControl(out _);

        var result = control.Begin("Paris", force: false);

        Assert.AreEqual(BeginResult.Started, result, "the first attempt must always start");
        Assert.IsTrue(control.IsClaimHeld, "the started attempt holds the claim");
    }

    [TestMethod]
    public void Begin_SecondCallBeforeEnd_ReturnsInFlightAndKeepsOthersClaim()
    {
        var control = CreateControl(out _);

        control.Begin("Paris", force: false);
        var second = control.Begin("Paris", force: false);

        Assert.AreEqual(BeginResult.InFlight, second, "the in-flight fetch owns the claim");
        Assert.IsTrue(control.IsClaimHeld, "the first claim must stay held for the in-flight fetch");
    }

    [TestMethod]
    public void Begin_WithinWindowWithoutStamp_ReturnsThrottledAndReleasesOwnClaim()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        Assert.IsTrue(control.Stamp("Paris"), "the first attempt stamps the throttle");
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = control.Begin("Paris", force: false);

        Assert.AreEqual(BeginResult.Throttled, second, "inside the 5-minute window the attempt cools down");
        Assert.IsFalse(control.IsClaimHeld, "a throttled attempt must release its own claim");
        Assert.IsFalse(control.IsWindowElapsed(), "the throttled attempt must not extend the window");
    }

    [TestMethod]
    public void Begin_WithinWindowForced_ReturnsStarted()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        Assert.IsTrue(control.Stamp("Paris"));
        clock.Advance(TimeSpan.FromMinutes(1));
        var forced = control.Begin("Paris", force: true);

        Assert.AreEqual(BeginResult.Started, forced, "a forced attempt (explicit user refresh) bypasses the window");
        Assert.IsTrue(control.IsClaimHeld);
    }

    [TestMethod]
    public void Begin_AfterWindowElapsed_ReturnsStarted()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        Assert.IsTrue(control.Stamp("Paris"));
        clock.Advance(TimeSpan.FromMinutes(5));
        var next = control.Begin("Paris", force: false);

        Assert.AreEqual(BeginResult.Started, next, "exactly 5 minutes elapses the window (>= comparison)");
        Assert.IsTrue(control.IsClaimHeld);
    }

    [TestMethod]
    public void End_ReleasesTheClaim()
    {
        var control = CreateControl(out _);

        control.Begin("Paris", force: false);
        control.End();

        Assert.IsFalse(control.IsClaimHeld, "the fetch's finally released the claim");
    }

    // ── IsWindowElapsed ──────────────────────────────────────────────────

    [TestMethod]
    public void IsWindowElapsed_NeverStamped_ReturnsTrue()
    {
        var control = CreateControl(out _);

        Assert.IsTrue(control.IsWindowElapsed(), "a never-fetched widget may fetch immediately");
    }

    [TestMethod]
    public void IsWindowElapsed_WithinWindow_ReturnsFalse()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        Assert.IsTrue(control.Stamp("Paris"));
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.IsFalse(control.IsWindowElapsed(), "4 minutes after a stamp the window has not elapsed");
    }

    // ── Stamp (the failure-path identity guard) ──────────────────────────

    [TestMethod]
    public void Stamp_MatchingQuery_StampsTheThrottle()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        clock.Advance(TimeSpan.FromMinutes(2));
        bool stamped = control.Stamp("Paris");

        Assert.IsTrue(stamped, "the identity still matches, so the attempt cools down");
        Assert.IsFalse(control.IsWindowElapsed(), "the stamp primed the 5-minute window");
    }

    [TestMethod]
    public void Stamp_DivergedQuery_ReturnsFalseWithoutStamping()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        control.AdvanceResolution("London"); // the identity changed mid-flight
        clock.Advance(TimeSpan.FromMinutes(2));
        bool stamped = control.Stamp("Paris");

        Assert.IsFalse(stamped, "the fetch ran for a key the identity no longer matches");
        Assert.IsTrue(control.IsWindowElapsed(), "a diverged fetch must not cool down the NEW identity");
    }

    // ── ConfirmAndStamp (the success-path compare + payload capture) ─────

    [TestMethod]
    public void ConfirmAndStamp_MatchingQuery_CapturesPayloadAndStamps()
    {
        var control = CreateControl(out var clock);
        IReadOnlyList<GeocodeCandidate> candidates = [Candidate("Paris, FR")];

        control.AdvanceResolution("Paris");
        control.SetCandidates(candidates);
        control.SetResolved(48.85, 2.35, "Paris", population: 2161000);
        clock.Advance(TimeSpan.FromMinutes(1));
        bool confirmed = control.ConfirmAndStamp("Paris", out IReadOnlyList<GeocodeCandidate> got, out double population);

        Assert.IsTrue(confirmed, "the identity matched, so the snapshot applies");
        Assert.AreEqual(1, got.Count, "the candidate list is carried out under the gate");
        Assert.AreEqual(2161000.0, population, "the resolved population is carried out under the gate");
        Assert.IsFalse(control.IsWindowElapsed(), "the success confirmed and stamped the window");
    }

    [TestMethod]
    public void ConfirmAndStamp_DivergedQuery_ReturnsFalseEmptyPayloadAndNoStamp()
    {
        var control = CreateControl(out var clock);

        control.SetCandidates([Candidate("Paris, FR")]);
        control.AdvanceResolution("London");
        clock.Advance(TimeSpan.FromMinutes(2));
        bool confirmed = control.ConfirmAndStamp("Paris", out IReadOnlyList<GeocodeCandidate> got, out double population);

        Assert.IsFalse(confirmed, "the identity changed mid-fetch; the snapshot is stale");
        Assert.AreEqual(0, got.Count, "no payload may leak out of a rejected confirm");
        Assert.AreEqual(0.0, population);
        Assert.IsTrue(control.IsWindowElapsed(), "a rejected confirm must not stamp the throttle");
    }

    // ── AdvanceResolution (key-change clears old identity) ───────────────

    [TestMethod]
    public void AdvanceResolution_ChangedQuery_ClearsOldCoordinatesNameAndPopulation()
    {
        var control = CreateControl(out _);

        control.SetCandidates([Candidate("Paris, FR")]);
        control.SetResolved(48.85, 2.35, "Paris", population: 2161000);
        control.AdvanceResolution("London");

        Assert.IsNull(control.Lat, "the old identity's coordinates must not survive a key change");
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.ResolvedCityName, "a stale place name must not trap the next editor");
        Assert.AreEqual(0.0, control.ResolvedPopulation, "the population reset rides the key change");
        Assert.AreEqual(1, control.Candidates.Count, "the candidate list survives a key change (it is cleared explicitly)");
    }

    [TestMethod]
    public void AdvanceResolution_SameQuery_KeepsCoordinatesAndResetsPopulation()
    {
        var control = CreateControl(out _);

        control.AdvanceResolution("Paris");
        control.SetResolved(48.85, 2.35, "Paris", population: 2161000);
        control.AdvanceResolution("Paris");

        Assert.AreEqual(48.85, control.Lat, "a re-fetch of the same identity keeps its coordinates");
        Assert.AreEqual(2.35, control.Lon);
        Assert.AreEqual("Paris", control.ResolvedCityName);
        Assert.AreEqual(0.0, control.ResolvedPopulation, "the pending-fetch population is always reset to unknown");
    }

    // ── coordinate / candidate maintenance ───────────────────────────────

    [TestMethod]
    public void ClearCoordinates_KeepsCandidatesAndPopulation()
    {
        var control = CreateControl(out _);

        control.SetCandidates([Candidate("Paris, FR", population: 5000)]);
        control.SetResolved(48.85, 2.35, "Paris", population: 2161000);
        control.ClearCoordinates();

        Assert.IsNull(control.Lat, "an ambiguous tie must not guess coordinates");
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.ResolvedCityName, "the previous place's name must not linger");
        Assert.AreEqual(1, control.Candidates.Count, "the pick list still resolves against the candidates it was offered from");
        Assert.AreEqual(2161000.0, control.ResolvedPopulation);
    }

    [TestMethod]
    public void ClearCandidates_DropsCandidatesAndPopulation()
    {
        var control = CreateControl(out _);

        control.SetCandidates([Candidate("Paris, FR", population: 5000)]);
        control.SetResolved(48.85, 2.35, "Paris", population: 2161000);
        control.ClearCandidates();

        Assert.AreEqual(0, control.Candidates.Count, "a changed location must not pick against old candidates");
        Assert.AreEqual(0.0, control.ResolvedPopulation);
    }

    // ── TryApplyCacheIdentity ────────────────────────────────────────────

    [TestMethod]
    public void TryApplyCacheIdentity_BootWithCachedName_AppliesNameAndPrimesThrottle()
    {
        var control = CreateControl(out _);

        bool applied = control.TryApplyCacheIdentity("Paris", 48.85, 2.35, "Paris", out string appliedName);

        Assert.IsTrue(applied, "an empty current query is the legitimate boot case");
        Assert.AreEqual("Paris", appliedName, "the payload's carried name wins over a composed fallback");
        Assert.AreEqual(48.85, control.Lat);
        Assert.IsFalse(control.IsWindowElapsed(), "a freshly cached widget must not immediately re-fetch");
    }

    [TestMethod]
    public void TryApplyCacheIdentity_BootWithoutName_FormatsTheCachedCoordinates()
    {
        var control = CreateControl(out _);

        bool applied = control.TryApplyCacheIdentity("Coords:48.85,2.35", 48.85, 2.35, null, out string appliedName);

        Assert.IsTrue(applied);
        Assert.AreEqual("48.85, 2.35", appliedName, "the coordinate form is the one spelling shared with the resolver");
    }

    [TestMethod]
    public void TryApplyCacheIdentity_BootWithoutNameOrCoordinates_UsesTheNeutralLabel()
    {
        var control = CreateControl(out _);

        bool applied = control.TryApplyCacheIdentity("Paris", null, null, null, out string appliedName);

        Assert.IsTrue(applied, "even a name-less coordinate-less payload is a valid boot");
        Assert.AreEqual("Unknown location", appliedName, "the neutral label is the single spelling");
    }

    [TestMethod]
    public void TryApplyCacheIdentity_DivergedQuery_RefusesAndLeavesStateUntouched()
    {
        var control = CreateControl(out _);

        control.AdvanceResolution("London");
        control.SetResolved(51.5, -0.12, "London", population: 8982000);
        bool applied = control.TryApplyCacheIdentity("Paris", 48.85, 2.35, "Paris", out string appliedName);

        Assert.IsFalse(applied, "a different identity's resolution has started; the payload must not apply");
        Assert.AreEqual("", appliedName);
        Assert.AreEqual(51.5, control.Lat, "the live state must survive a refused apply");
        Assert.AreEqual("London", control.ResolvedCityName);
        Assert.AreEqual(8982000.0, control.ResolvedPopulation);
    }

    [TestMethod]
    public void TryApplyCacheIdentity_MatchingQuery_Applies()
    {
        var control = CreateControl(out _);

        control.AdvanceResolution("Paris");
        bool applied = control.TryApplyCacheIdentity("Paris", 48.85, 2.35, "Paris", out string appliedName);

        Assert.IsTrue(applied, "the same identity may apply its cache");
        Assert.AreEqual("Paris", appliedName);
        Assert.AreEqual(48.85, control.Lat);
    }

    // ── Invalidate ───────────────────────────────────────────────────────

    [TestMethod]
    public void Invalidate_ResetsCoordinatesNameQueryAndThrottleButKeepsCandidates()
    {
        var control = CreateControl(out var clock);

        control.AdvanceResolution("Paris");
        control.SetCandidates([Candidate("Paris, FR")]);
        control.SetResolved(48.85, 2.35, "Paris", population: 2161000);
        Assert.IsTrue(control.Stamp("Paris"));
        clock.Advance(TimeSpan.FromMinutes(6));

        control.Invalidate();

        Assert.IsNull(control.Lat, "a location change must re-resolve from scratch");
        Assert.IsNull(control.Lon);
        Assert.AreEqual("", control.ResolvedCityName);
        Assert.AreEqual("", control.LastLocationQuery, "the identity is fully reset");
        Assert.IsTrue(control.IsWindowElapsed(), "the next fetch must run immediately");
        Assert.AreEqual(1, control.Candidates.Count, "the pick list survives a location invalidation");
    }
}
