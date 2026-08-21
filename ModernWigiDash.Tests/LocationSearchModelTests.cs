using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.Tests;

/// <summary>
/// The <see cref="LocationSearchModel"/> rules — pure, no WPF: the seed text
/// (label + compact population suffix), the tick's query and the commit text
/// (the seeded suffix searches/commits as the base label; a real edit takes
/// over verbatim), the focus-loss veto for a pick in progress, and the
/// debounce tick's version-token stale rule.
/// </summary>
[TestClass]
public class LocationSearchModelTests
{
    // ── Seed text ────────────────────────────────────────────────

    [TestMethod]
    public void SeedText_BaseLabelAlone_WhenNoPopulationKnown()
    {
        Assert.AreEqual("Miami, Florida", LocationSearchModel.SeedText("Miami, Florida", null));
        Assert.AreEqual("Miami, Florida", LocationSearchModel.SeedText("Miami, Florida", 0));
    }

    [TestMethod]
    public void SeedText_EmptyLabel_NeverSeedsABareSuffix()
    {
        Assert.AreEqual(string.Empty, LocationSearchModel.SeedText(string.Empty, 8_400_000.0));
    }

    [TestMethod]
    public void SeedText_AppendsTheCompactPopulationSuffix()
    {
        Assert.AreEqual("New York, New York, United States · 8.4M",
            LocationSearchModel.SeedText("New York, New York, United States", 8_400_000.0));
    }

    // ── Query + commit: the display-only suffix rule ─────────────

    [TestMethod]
    public void QueryFor_SeededTextBoxText_SearchesTheBaseLabel()
    {
        string seed = "NYC · 8.4M";
        Assert.AreEqual("NYC", LocationSearchModel.QueryFor(seed, seed, "NYC"),
            "the suffix searches nothing — the seeded box searches the base label");
    }

    [TestMethod]
    public void QueryFor_TypedText_SearchesVerbatimTrimmed()
    {
        Assert.AreEqual("Berlin, NH", LocationSearchModel.QueryFor("  Berlin, NH  ", "NYC · 8.4M", "NYC"));
    }

    [TestMethod]
    public void CommitText_SeededTextBoxText_CommitsTheBaseLabel()
    {
        string seed = "NYC · 8.4M";
        Assert.AreEqual("NYC", LocationSearchModel.CommitText(seed, seed, "NYC"),
            "committing the suffix verbatim would degrade the next resolution to a bare-name tie");
    }

    [TestMethod]
    public void CommitText_TypedText_CommitsVerbatim()
    {
        Assert.AreEqual("Berlin, New Hampshire", LocationSearchModel.CommitText("Berlin, New Hampshire", "NYC · 8.4M", "NYC"));
    }

    // ── Focus-loss veto ──────────────────────────────────────────

    [TestMethod]
    public void ShouldCommitOnLostFocus_VetoesAPickInProgress()
    {
        Assert.IsFalse(LocationSearchModel.ShouldCommitOnLostFocus(true, false),
            "a mouse press inside the popup is a pick in progress — no commit");
        Assert.IsFalse(LocationSearchModel.ShouldCommitOnLostFocus(false, true),
            "keyboard navigation with the popup open also vetoes the commit");
        Assert.IsTrue(LocationSearchModel.ShouldCommitOnLostFocus(false, false));
    }

    // ── Tick version rule ────────────────────────────────────────

    [TestMethod]
    public async Task RunSearchTickAsync_CompletedSearch_ReturnsCandidates()
    {
        var fake = new InspectorPanelRendererTests.ScriptableLocationSearch();
        var version = new SearchVersionToken();

        var search = LocationSearchModel.RunSearchTickAsync(fake, "Berlin", version);
        fake.Complete("Berlin", new GeocodeCandidate("Berlin, Germany", "Berlin, Germany", 52.52, 13.405));
        var (outcome, candidates) = await search;

        Assert.AreEqual(LocationSearchTick.Success, outcome);
        Assert.AreEqual(1, candidates!.Count);
        Assert.AreEqual("Berlin, Germany", candidates[0].Label);
    }

    [TestMethod]
    public async Task RunSearchTickAsync_ShortQueryTick_InvalidatesInFlightResponse()
    {
        var fake = new InspectorPanelRendererTests.ScriptableLocationSearch();
        var version = new SearchVersionToken();

        var inFlight = LocationSearchModel.RunSearchTickAsync(fake, "be", version);
        var (shortOutcome, _) = await LocationSearchModel.RunSearchTickAsync(fake, "x", version);

        Assert.AreEqual(LocationSearchTick.NoSearch, shortOutcome,
            "a query shorter than two characters must not search");

        fake.Complete("be", new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508));
        var (staleOutcome, staleCandidates) = await inFlight;

        Assert.AreEqual(LocationSearchTick.Stale, staleOutcome,
            "a short-query tick must invalidate the response still in flight from the longer query");
        Assert.IsNull(staleCandidates);
    }

    // ── Population format ────────────────────────────────────────

    [TestMethod]
    public void FormatPopulation_CompactTiersInInvariantCulture()
    {
        Assert.AreEqual("940", LocationSearchModel.FormatPopulation(940.0));
        Assert.AreEqual("9.4k", LocationSearchModel.FormatPopulation(9_400.0));
        Assert.AreEqual("8.4M", LocationSearchModel.FormatPopulation(8_400_000.0));
    }
}
