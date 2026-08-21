namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the widget's resolved-identity module: the dropdown candidates, the
/// resolved population, the header city name, and the pending label
/// write-back, plus the two invalidation rules that mirror the client's
/// InvalidateCoordinates / InvalidateLocation. The display-state module keeps
/// only the gate discipline (every mutation runs under its one gate) and the
/// UI-thread write-back flush; the state transitions themselves live here.
/// </summary>
[TestClass]
public class WeatherResolvedIdentityTests
{
    private static readonly GeocodeCandidate Berlin
        = new("Berlin, State of Berlin, Germany", "Berlin, State of Berlin, Germany", 52.52437, 13.41053);

    private static WeatherResolvedIdentity Resolved()
        => new("Unknown location");

    [TestMethod]
    public void Ctor_NeutralLabel_IsTheInitialCityName()
    {
        var identity = new WeatherResolvedIdentity("Unknown location");

        Assert.AreEqual("Unknown location", identity.CityName,
            "the header must show the neutral label until a resolution sets a real identity");
        Assert.AreEqual(0, identity.Candidates.Count);
        Assert.AreEqual(0, identity.Population);
        Assert.IsNull(identity.PendingWriteback);
    }

    [TestMethod]
    public void Apply_ProvidedValues_ReplacePrevious()
    {
        var identity = Resolved();
        var next = new GeocodeCandidate("Victoria, British Columbia, Canada", "Victoria, British Columbia, Canada", 48.4284, -123.3656);

        identity.Apply([Berlin], 3_426_354, "Berlin, State of Berlin, Germany");
        identity.Apply([next], 335_696, "Victoria, British Columbia, Canada");

        Assert.AreEqual(1, identity.Candidates.Count);
        Assert.AreEqual("Victoria, British Columbia, Canada", identity.Candidates[0].Label);
        Assert.AreEqual(335_696, identity.Population);
        Assert.AreEqual("Victoria, British Columbia, Canada", identity.CityName);
    }

    [TestMethod]
    public void Apply_NullArguments_KeepPreviousValues()
    {
        var identity = Resolved();
        identity.Apply([Berlin], 3_426_354, "Berlin, State of Berlin, Germany");

        // The fetch reported none of the three: nothing may change (the
        // "response omitted this section — keep the previous value" rule).
        identity.Apply();

        Assert.AreEqual("Berlin, State of Berlin, Germany", identity.CityName);
        Assert.AreEqual(3_426_354, identity.Population);
        Assert.AreEqual(1, identity.Candidates.Count);
    }

    [TestMethod]
    public void Apply_ZeroPopulation_ClearsTheResolvedPopulation()
    {
        var identity = Resolved();
        identity.Apply([Berlin], 3_426_354, "Berlin, State of Berlin, Germany");

        // The client's no-data sentinel: 0 clears, null keeps.
        identity.Apply(population: 0);

        Assert.AreEqual(0, identity.Population);
        Assert.AreEqual("Berlin, State of Berlin, Germany", identity.CityName,
            "only the population clears; the name and candidates are untouched");
    }

    [TestMethod]
    public void InvalidateCoordinates_KeepsCandidates_DropsNameAndPopulation()
    {
        var identity = Resolved();
        identity.Apply([Berlin], 3_426_354, "Berlin, State of Berlin, Germany");
        identity.SetPendingWriteback("Berlin, State of Berlin, Germany");

        identity.InvalidateCoordinates();

        Assert.AreEqual(1, identity.Candidates.Count,
            "a Location Match pick resolves against the candidates it was offered from, so they survive");
        Assert.AreEqual("", identity.CityName);
        Assert.AreEqual(0, identity.Population);
        Assert.IsNull(identity.PendingWriteback,
            "the invalidation must also drop a pending write-back so the next render cannot flush the old label");
    }

    [TestMethod]
    public void InvalidateLocation_DropsCandidatesNameAndPopulation()
    {
        var identity = Resolved();
        identity.Apply([Berlin], 3_426_354, "Berlin, State of Berlin, Germany");
        identity.SetPendingWriteback("Berlin, State of Berlin, Germany");

        identity.InvalidateLocation();

        Assert.AreEqual(0, identity.Candidates.Count,
            "every other location input voids the whole identity so a stale pick can never win");
        Assert.AreEqual("", identity.CityName);
        Assert.AreEqual(0, identity.Population);
        Assert.IsNull(identity.PendingWriteback);
    }

    [TestMethod]
    public void SetPendingWriteback_ThenTake_ReturnsAndClears()
    {
        var identity = Resolved();
        identity.SetPendingWriteback("Berlin, State of Berlin, Germany");

        string? taken = identity.TakePendingWriteback();

        Assert.AreEqual("Berlin, State of Berlin, Germany", taken);
        Assert.IsNull(identity.PendingWriteback,
            "the pending field clears with the take so a re-entrant render cannot double-write");
    }

    [TestMethod]
    public void TakePendingWriteback_WhenNone_ReturnsNull()
    {
        var identity = Resolved();

        Assert.IsNull(identity.TakePendingWriteback());
    }
}
