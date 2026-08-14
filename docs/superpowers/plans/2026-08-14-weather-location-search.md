# Weather Location Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the weather widget's plain Location text field with a search-as-you-type control (cities + ZIPs), persist picks as exact coordinates, and never display weather for an ambiguous bare name.

**Architecture:** The geocoder gains a public search API on `WeatherClient` (`SearchCitiesAsync`) and an ambiguity signal on resolution. The widget exposes the search through a new capability contract (`IWidgetLocationSearch`); the inspector renders a search editor (TextBox + results popup, debounced) when the widget implements it, committing picks through the existing write-back funnel. Ambiguous bare names never fetch — the widget shows a "select which one" state instead.

**Tech Stack:** .NET 10 / C# 14, WPF inspector, Open-Meteo geocoding API, MSTest. House rules: one type per file, file-scoped namespaces, the `SetProperty`/`PersistProperty` invariant for all widget-property mutations, MSTest `MethodName_Scenario_ExpectedResult` naming.

## Global Constraints

- All widget-property mutations route through `ModernWidgetBase.SetProperty` (protected) or the context's `PersistProperty` — never direct field writes (CONTEXT.md: the SetProperty/PersistProperty invariant).
- The inspector never branches on widget types — capability contracts only (`IWidgetPropertyOptionsProvider` / `IWidgetEditorProvider` precedent).
- `WeatherClient` never throws on network/parse errors from search/resolution — empty list / null snapshot instead (the existing never-throws rule).
- The transport is synchronous (ADR-0001) — untouched by this feature.
- Test command (app may be running): `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`

## File Structure

- `ModernWigiDash.Widgets/WeatherClient.cs` — add `SearchCitiesAsync`, `LastResolutionAmbiguous`, geocoder extraction, `GeocodeCandidate.Population` (init).
- `ModernWigiDash.Widgets/IWidgetLocationSearch.cs` (new) — the search capability contract.
- `ModernWigiDash.Widgets/WeatherForecastWidget.cs` — implements the contract; the `_needsLocationSelection` gate state + render prompt.
- `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs` — `EditorKind.LocationSearch` editor (search box + results popup).
- `ModernWigiDash.App/Inspector/InspectorController.cs` — `CommitLocationPick` callback plumbing.
- `ModernWigiDash.Widgets/IWidgetEditorProvider.cs` — `EditorKind.LocationSearch` enum member.
- `ModernWigiDash.App/MainWindow.xaml.cs` — bind `CommitLocationPick`.
- Tests: `WeatherClientTests.cs`, `WeatherForecastWidgetTests.cs`, `InspectorEditorProviderTests.cs`, `InspectorPanelRendererTests.cs` (additions).

---

### Task 1: `WeatherClient.SearchCitiesAsync` + candidate population

**Files:**
- Modify: `ModernWigiDash.Widgets/WeatherClient.cs` (GeocodeCandidate record ~line 65; geocode method ~lines 548-630)
- Test: `ModernWigiDash.Tests/WeatherClientTests.cs`

**Interfaces:**
- Produces: `public async Task<IReadOnlyList<GeocodeCandidate>> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)` — returns `[]` on error, never throws.
- Produces: `GeocodeCandidate` gains `public double Population { get; init; }` (default 0 — positional ctor unchanged).
- Produces: private `static Uri BuildGeocodeSearchUri(string query, string? countryCode)` — the shared URL builder.

- [ ] **Step 1: Write the failing tests**

Add to `WeatherClientTests.cs` (after the existing geocode tests; reuse `CreateClient` and the `StubHttpHandler` patterns already in the file):

```csharp
[TestMethod]
public async Task SearchCitiesAsync_MapsCandidatesWithPopulation()
{
    var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleBerlines));
    var client = CreateClient(stub);

    var results = await client.SearchCitiesAsync("Berl", CancellationToken.None);

    Assert.AreEqual(5, results.Count);
    var first = results[0];
    Assert.AreEqual("Berlin, State of Berlin, Germany", first.Label);
    Assert.AreEqual(52.52437, first.Lat, 0.0001);
    Assert.AreEqual(3426354, first.Population);
}

[TestMethod]
public async Task SearchCitiesAsync_HttpError_ReturnsEmptyList()
{
    var stub = new StubHttpHandler(_ => StubHttpHandler.NotFound());
    var client = CreateClient(stub);

    var results = await client.SearchCitiesAsync("Berlin", CancellationToken.None);

    Assert.IsNotNull(results);
    Assert.AreEqual(0, results.Count, "a failed search must not throw");
}

[TestMethod]
public async Task SearchCitiesAsync_Cancelled_ThrowsOperationCanceled()
{
    var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleBerlines));
    var client = CreateClient(stub);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsExceptionAsync<OperationCanceledException>(
        () => client.SearchCitiesAsync("Berlin", cts.Token));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~SearchCitiesAsync"`
Expected: FAIL — `SearchCitiesAsync` does not exist.

- [ ] **Step 3: Implement**

In `WeatherClient.cs`:

1. Extend the record (existing line ~65):

```csharp
public sealed record GeocodeCandidate(string Label, string Query, double Lat, double Lon)
{
    /// <summary>Candidate population (the search list's disambiguating label
    /// data; 0 when the geocoder omitted it).</summary>
    public double Population { get; init; }
}
```

2. Extract the URL builder and add the search method (place near `GeocodeCityLocationAsync`):

```csharp
/// <summary>The geocoder search URL — the single URL builder shared by the
/// resolution flow and the inspector's search-as-you-type (cities and postal
/// codes both resolve as a name query).</summary>
private static Uri BuildGeocodeSearchUri(string query, string? countryCode)
{
    string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=10&language=en&format=json";
    if (!string.IsNullOrWhiteSpace(countryCode))
        url += $"&countryCode={Uri.EscapeDataString(countryCode.Trim())}";
    return new Uri(url);
}

/// <summary>
/// The inspector's search-as-you-type surface: geocodes <paramref name="query"/>
/// (a city name or a postal code) into ranked candidates with their exact
/// coordinates and population. Returns an empty list on any failure — never
/// throws; cancellation propagates so the editor can discard stale responses.
/// </summary>
public async Task<IReadOnlyList<GeocodeCandidate>> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)
{
    try
    {
        string json = await Http.GetStringAsync(BuildGeocodeSearchUri(query, null), cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            return [];
        }

        var candidates = new List<GeocodeCandidate>(results.GetArrayLength());
        foreach (var candidate in results.EnumerateArray())
        {
            string label = ComposeResolvedName(candidate, query);
            double population = candidate.TryGetProperty("population", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetDouble()
                : 0;
            candidates.Add(new GeocodeCandidate(
                label, label,
                candidate.GetProperty("latitude").GetDouble(),
                candidate.GetProperty("longitude").GetDouble())
            {
                Population = population,
            });
        }
        return candidates;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logError?.Invoke($"Location search failed for '{SanitizeLog(query)}': {ex.Message}", ex);
        return [];
    }
}
```

3. Refactor `GeocodeCityLocationAsync` (line ~550) to use the shared URI builder for its search URL (replace its inline URL construction with `BuildGeocodeSearchUri(namePart, location.CountryCode)` — the URL must remain byte-identical: `name={namePart}&count=10&language=en&format=json` plus the optional `&countryCode=`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: the same filter as Step 2.
Expected: PASS (all 3 new tests + the existing geocode tests still pass).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/WeatherClient.cs ModernWigiDash.Tests/WeatherClientTests.cs
git commit -m "feat: WeatherClient.SearchCitiesAsync (search-as-you-type + candidate population)"
```

---

### Task 2: The ambiguity gate in `WeatherClient`

**Files:**
- Modify: `ModernWigiDash.Widgets/WeatherClient.cs` (geocode ranking ~lines 570-620)
- Test: `ModernWigiDash.Tests/WeatherClientTests.cs`

**Interfaces:**
- Produces: `internal bool LastResolutionAmbiguous { get; private set; }` — true when the last geocode resolution ended in a population-decided tie with no `LocationMatch` pick.
- Consumes: `GeocodeCandidate` with `Population` (Task 1).

- [ ] **Step 1: Write the failing tests**

In `WeatherClientTests.cs`, replace the `FetchCurrentAsync_AmbiguousBareName_PicksHighestPopulationSameNamedCity` test (added earlier this session) with the gate behavior, and add two more:

```csharp
[TestMethod]
public async Task FetchCurrentAsync_AmbiguousBareName_ReturnsNullAndFlagsAmbiguity()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    // A bare "Berlin" ties four candidates on the exact name; without a pick
    // the population choice is untrustworthy — wrong data must never display.
    var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));

    Assert.IsNull(snapshot, "an ambiguous bare name must not fetch weather");
    Assert.IsTrue(client.LastResolutionAmbiguous, "the ambiguity must be signalled to the widget");
}

[TestMethod]
public async Task FetchCurrentAsync_AmbiguousName_WithLocationMatch_FetchesThePick()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    // A persisted Location Match pick resolves the tie deterministically.
    var snapshot = await client.FetchCurrentAsync(new WeatherLocation(
        "Fixed Location", "Berlin", null, null, null) { LocationMatch = "Berlin, New Hampshire, United States" });

    Assert.IsNotNull(snapshot);
    Assert.AreEqual(44.46867, snapshot.Lat, 0.0001, "the picked Berlin, NH must win over the population choice");
    Assert.AreEqual(-71.18508, snapshot.Lon, 0.0001);
    Assert.IsFalse(client.LastResolutionAmbiguous, "a resolved pick is not ambiguous");
}

[TestMethod]
public async Task FetchCurrentAsync_UnambiguousName_DoesNotFlagAmbiguity()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    // "Victoria" ties no candidate on the exact name — the exact-match winner
    // is unambiguous and fetches instantly.
    var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null));

    Assert.IsNotNull(snapshot);
    Assert.IsFalse(client.LastResolutionAmbiguous);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~FetchCurrentAsync_AmbiguousBareName|FullyQualifiedName~FetchCurrentAsync_AmbiguousName_WithLocationMatch|FullyQualifiedName~FetchCurrentAsync_UnambiguousName"`
Expected: FAIL — the bare-name test still resolves to Berlin DE (the old behavior).

- [ ] **Step 3: Implement**

In `GeocodeCityLocationAsync` (the ranking loop at ~line 570-585), restructure the winner selection:

```csharp
// Every candidate becomes a pickable option ("Location Match" dropdown) ...
// (unchanged: build `candidates` with ComposeResolvedName)

LastResolutionAmbiguous = false;

// The persisted Location Match pick resolves the tie deterministically (the
// single promotion rule — see the existing block below).
// (unchanged: the LocationMatch promotion block stays FIRST — when it
//  matches, set _lat/_lon/_resolvedCityName and return)

// Rank: collect (score, population, candidate) and detect a population-decided
// tie — when more than one candidate shares the top score, the winner is
// untrustworthy without a pick (the "Berlin" problem). The widget must not
// display wrong-city weather, so coordinates stay unresolved and the fetch
// returns null (the existing no-coordinates path in FetchCurrentAsync).
var ranked = results.EnumerateArray()
    .Select(c => (Candidate: c, Rank: RankGeocodeCandidate(c, namePart, suffixPart, location.CountryCode)))
    .ToList();
int bestScore = ranked.Max(r => r.Rank.Score);
var topTied = ranked.Where(r => r.Rank.Score == bestScore).ToList();
if (topTied.Count > 1)
{
    LastResolutionAmbiguous = true;
    return;
}

JsonElement best = topTied[0].Candidate;
double bestPopulation = topTied[0].Rank.Population;
_lat = best.GetProperty("latitude").GetDouble();
_lon = best.GetProperty("longitude").GetDouble();
_resolvedCityName = ComposeResolvedName(best, namePart);
```

Preserve the existing behavior exactly for the single-winner case (score/population ordering is no longer needed since a tie is the only population-decided case — but keep the old `bestScore`/`bestPopulation` walk for the single-winner selection if simpler; the assertion is: exactly the old winner when there is no tie).

Also: set `LastResolutionAmbiguous = false` at the top of `FetchCurrentAsync` (before resolution) so a stale flag never survives a successful fetch; the successful coordinate path leaves it false.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 filter, then the full `WeatherClientTests` filter.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/WeatherClient.cs ModernWigiDash.Tests/WeatherClientTests.cs
git commit -m "feat: ambiguity gate — an untrusted location tie never fetches weather"
```

---

### Task 3: `IWidgetLocationSearch` + the widget's select-which-one state

**Files:**
- Create: `ModernWigiDash.Widgets/IWidgetLocationSearch.cs`
- Modify: `ModernWigiDash.Widgets/WeatherForecastWidget.cs`
- Test: `ModernWigiDash.Tests/WeatherForecastWidgetTests.cs` (uses the existing `PersistingContext` test double from TestDoubles.cs)

**Interfaces:**
- Produces: `public interface IWidgetLocationSearch` with `Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)` and `void CommitPick(GeocodeCandidate candidate)`.
- Consumes: `WeatherClient.SearchCitiesAsync` (Task 1), `WeatherClient.LastResolutionAmbiguous` (Task 2), `ModernWidgetBase.SetProperty` (protected — used inside the widget).

- [ ] **Step 1: Write the failing tests**

Create `IWidgetLocationSearch.cs` first (the tests reference it):

```csharp
namespace ModernWigiDash.Widgets;

/// <summary>
/// Optional weather-widget capability: search-as-you-type location selection.
/// The inspector renders the search editor and commits picks through this
/// contract — never by branching on the widget type. <see cref="CommitPick"/>
/// writes the picked place's label and exact coordinates through the widget's
/// SetProperty funnel (the invariant), so the persisted profile is
/// deterministic across restarts.
/// </summary>
public interface IWidgetLocationSearch
{
    /// <summary>Geocodes <paramref name="query"/> (city name or postal code)
    /// into ranked candidates with exact coordinates; empty on error.</summary>
    Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct);

    /// <summary>Commits a picked candidate: Location = label, Latitude/Longitude
    /// = exact coordinates, LocationMatch cleared — via SetProperty.</summary>
    void CommitPick(GeocodeCandidate candidate);
}
```

In `WeatherForecastWidgetTests.cs`:

```csharp
[TestMethod]
public void CommitPick_SetsLocationLatLonAndClearsLocationMatch()
{
    var widget = new WeatherForecastWidget();
    var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

    var search = (IWidgetLocationSearch)widget;
    search.CommitPick(new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508));

    Assert.AreEqual("Berlin, New Hampshire, United States", widget.Location);
    Assert.AreEqual("44.46867", widget.Latitude);
    Assert.AreEqual("-71.18508", widget.Longitude);
    Assert.AreEqual("", widget.LocationMatch);
    Assert.IsTrue(placed.PropertyValues.ContainsKey("Latitude"), "the pick must persist through SetProperty");
    Assert.AreEqual("44.46867", placed.PropertyValues["Latitude"]);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~CommitPick_SetsLocationLatLonAndClearsLocationMatch"`
Expected: FAIL — the widget does not implement the interface.

- [ ] **Step 3: Implement**

1. Create `ModernWigiDash.Widgets/IWidgetLocationSearch.cs` with the content from Step 1.
2. In `WeatherForecastWidget.cs`:
   - Change the class declaration to `: ModernWidgetBase, IWidgetPropertyOptionsProvider, IWidgetLocationSearch`.
   - Add the two members:

```csharp
// ── IWidgetLocationSearch ────────────────────────────────────────────────

public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
    => _client.SearchCitiesAsync(query, ct);

public void CommitPick(GeocodeCandidate candidate)
{
    SetProperty(nameof(Location), candidate.Label);
    SetProperty(nameof(Latitude), candidate.Lat.ToString("F5", CultureInfo.InvariantCulture));
    SetProperty(nameof(Longitude), candidate.Lon.ToString("F5", CultureInfo.InvariantCulture));
    SetProperty(nameof(LocationMatch), "");
}
```

(`SetProperty` fires `OnPropertyChanged` per write — the Location change forces the re-fetch through the existing handler; the final state carries the exact coordinates.)

- [ ] **Step 4: Run the test to verify it passes**

Run: the Step 2 filter.
Expected: PASS.

- [ ] **Step 5: Add the gate-state tests (widget)**

```csharp
[TestMethod]
public async Task FetchLiveWeatherAsync_AmbiguousResolution_SetsSelectState()
{
    // A client whose resolution flags ambiguity must leave the widget in the
    // "select which one" state — not stale or wrong data. The client's
    // TestHttpClient seam returns the real Berlin candidate set; the gate
    // blocks the forecast fetch, so the flag is observed after the null
    // snapshot.
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return StubHttpHandler.NotFound();
    });
    var widget = new WeatherForecastWidget { Location = "Berlin" };
    widget.TestHttpClient = stub.HttpClient;
    var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

    await widget.FetchLiveWeatherAsync();

    Assert.IsTrue(widget._needsLocationSelection, "an ambiguous bare name must land in the select-which-one state");
}

[TestMethod]
public async Task FetchLiveWeatherAsync_UnambiguousResolution_ClearsSelectState()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherForecastWidgetTests.SampleForecast) : StubHttpHandler.NotFound();
    });
    var widget = new WeatherForecastWidget { Location = "Victoria" };    widget.TestHttpClient = stub.HttpClient;
    var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
    widget._needsLocationSelection = true;

    await widget.FetchLiveWeatherAsync();

    Assert.IsFalse(widget._needsLocationSelection, "a clean resolution must clear the select state");
}

[TestMethod]
public void Render_WhenNeedsLocationSelection_DrawsPromptInsteadOfData()
{
    using var surface = SKSurface.Create(new SKImageInfo(406, 296));
    var canvas = surface.Canvas;
    var widget = new WeatherForecastWidget { Location = "Berlin", _needsLocationSelection = true };

    widget.Render(canvas, new SKRect(0, 0, 406, 296)); // must not throw, no data drawn

    Assert.IsTrue(true); // smoke: the prompt path renders without exceptions
}
```

(The tests reference `SampleBerlines`/`SampleSameNameMultiCountry` — make the fixtures `internal` in `WeatherClientTests` or move them to `TestDoubles.cs` as shared constants, matching the house pattern; `TestHttpClient` and `_needsLocationSelection` are the widget's internal test seams.)

- [ ] **Step 6: Implement the gate state**

In `WeatherForecastWidget.cs`:

1. Add the field (internal — the widget's existing test-seam convention): `internal bool _needsLocationSelection;`
2. In `FetchLiveWeatherAsync` after the null-snapshot check:

```csharp
var snapshot = await _client.FetchCurrentAsync(BuildLocation(), force, _pollCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
if (snapshot is null)
{
    // The ambiguity gate: an untrusted location tie never shows weather.
    if (_client.LastResolutionAmbiguous)
    {
        _needsLocationSelection = true;
        Context?.RequestRender();
    }
    return;
}
_needsLocationSelection = false;
```

3. In `Render`, after the paint creation and before the fetch kick:

```csharp
if (_needsLocationSelection)
{
    TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, $"{Location} — select which one",
        "Open the inspector and pick the exact place", text);
    return;
}
```

(`text` is already computed at the top of Render; place the check before `RequestRefresh()`.)

4. Clear the flag in `OnPropertyChanged` for `Location`, `Latitude`, `Longitude`, `CountryCode` (the same branch that forces the re-fetch): `_needsLocationSelection = false;` before `RequestRefresh(force: true)`.

- [ ] **Step 7: Run all widget tests**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~WeatherForecastWidgetTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add ModernWigiDash.Widgets/IWidgetLocationSearch.cs ModernWigiDash.Widgets/WeatherForecastWidget.cs ModernWigiDash.Tests/WeatherForecastWidgetTests.cs
git commit -m "feat: weather location search contract + select-which-one gate state"
```

---

### Task 4: The inspector search editor

**Files:**
- Modify: `ModernWigiDash.Widgets/IWidgetEditorProvider.cs` (enum)
- Modify: `ModernWigiDash.Widgets/WeatherForecastWidget.cs` (implement `IWidgetEditorProvider`)
- Modify: `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs`
- Modify: `ModernWigiDash.App/Inspector/InspectorController.cs`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs`
- Test: `ModernWigiDash.Tests/InspectorEditorProviderTests.cs`, `ModernWigiDash.Tests/InspectorPanelRendererTests.cs`

**Interfaces:**
- Produces: `EditorKind.LocationSearch` enum member.
- Produces: `InspectorCallbacks.CommitLocationPick` — `Action<GeocodeCandidate>?` init property.
- Produces: `InspectorController(InspectorControllerHost, DialogHost, Action? onProfileChanged = null, Action<GeocodeCandidate>? commitLocationPick = null)`.
- Consumes: `IWidgetLocationSearch` (Task 3), `InspectorCallbacks` (existing).

- [ ] **Step 1: Write the failing tests**

In `InspectorEditorProviderTests.cs` (mirror the existing `GetEditorKind`-style tests):

```csharp
[TestMethod]
public void WeatherWidget_LocationProperty_ReportsLocationSearchEditorKind()
{
    var prop = typeof(WeatherForecastWidget).GetProperty("Location")!;
    var widget = new WeatherForecastWidget();

    var kind = (widget as IWidgetEditorProvider)?.GetEditorKind(prop);

    Assert.AreEqual(EditorKind.LocationSearch, kind);
}
```

In `InspectorPanelRendererTests.cs` (or the editor-provider test file — match the existing renderer test pattern; the visual-tree walk uses the `FindVisualChildren`-style helper already present in `ThemeDialogTests`):

```csharp
[TestMethod]
public void Render_LocationSearchWidget_BuildsSearchEditorAndCommitsPick()
{
    var widget = new WeatherForecastWidget();
    var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
    var descriptions = InspectorModelBuilder.Describe(placed);
    var target = new StackPanel();
    GeocodeCandidate? committed = null;
    var callbacks = new InspectorCallbacks
    {
        TryFindResource = _ => null,
        ApplyInspectorPropertyValue = (_, _) => { },
        ShowIconSelectorPopup = null,
        AttachDropdownWithinWindow = null,
        CommitLocationPick = c => committed = c,
    };

    InspectorPanelRenderer.Render(placed, descriptions, target.Children, () => false, callbacks);

    // The Location row hosts the search editor: walk the built rows for the
    // results ListBox, select a candidate, and assert the commit callback ran.
    var listBox = FindVisualChildren<ListBox>(target).First();
    listBox.ItemsSource = new[] { new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508) };
    listBox.SelectedItem = listBox.Items[0];

    Assert.IsNotNull(committed, "picking from the search list must reach the commit callback");
    Assert.AreEqual("Berlin, New Hampshire, United States", committed!.Label);
}

private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
{
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is T typed) yield return typed;
        foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
    }
}
```

(The `FindVisualChildren` helper already exists in `ThemeDialogTests` — reuse it or duplicate the 8-line local, matching the house pattern.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~LocationSearch|FullyQualifiedName~LocationSearchWidget"`
Expected: FAIL — `EditorKind.LocationSearch` does not exist.

- [ ] **Step 3: Implement the enum + widget capability**

1. `IWidgetEditorProvider.cs` — add to the enum: `LocationSearch`.
2. `WeatherForecastWidget.cs` — add `IWidgetEditorProvider` to the class declaration and:

```csharp
public EditorKind? GetEditorKind(PropertyInfo property)
    => property.Name == nameof(Location) ? EditorKind.LocationSearch : null;
```

(Add `using System.Reflection;` if not present.)

- [ ] **Step 4: Implement the callbacks + controller wiring**

1. `InspectorPanelRenderer.cs` — add to `InspectorCallbacks`:

```csharp
/// <summary>Commits a location-search pick (label + exact coordinates) to the
/// selected widget through its IWidgetLocationSearch contract.</summary>
public Action<GeocodeCandidate>? CommitLocationPick { get; init; }
```

2. `InspectorController.cs` — extend the ctor with `Action<GeocodeCandidate>? commitLocationPick = null`; in `Refresh`'s `InspectorCallbacks` initializer add `CommitLocationPick = commitLocationPick,`.

- [ ] **Step 5: Implement the search editor in the renderer**

In `InspectorPanelRenderer.Render`'s switch, before the `default` Text/Number case, add:

```csharp
case WidgetPropertyType.Text when provider?.GetEditorKind(desc.Property) == EditorKind.LocationSearch:
    if (widget.ActiveInstance is IWidgetLocationSearch search)
    {
        propPanel.Children.Add(BuildLocationSearchEditor(desc, search, callbacks));
        break;
    }
    goto default;
```

Add the builder (near `BuildTextEditor`):

```csharp
/// <summary>
/// The search-as-you-type Location editor: a TextBox with a results popup,
/// debounced (300 ms), stale responses discarded by a version token. Enter or
/// focus loss commits the typed text as the property value (the ambiguity
/// gate then decides whether it may fetch); picking a result commits the
/// candidate's exact place through <see cref="InspectorCallbacks.CommitLocationPick"/>.
/// </summary>
private static StackPanel BuildLocationSearchEditor(EditorDescription desc, IWidgetLocationSearch search, InspectorCallbacks callbacks)
{
    var box = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
    var results = new ListBox { MaxHeight = 160, Visibility = Visibility.Collapsed };
    var popup = new Popup
    {
        PlacementTarget = box,
        Placement = PlacementMode.Bottom,
        StaysOpen = false,
        AllowsTransparency = true,
        Child = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = results
        }
    };

    int version = 0;
    var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    debounce.Tick += async (_, _) =>
    {
        debounce.Stop();
        string query = box.Text.Trim();
        if (query.Length < 2)
        {
            results.ItemsSource = null;
            popup.IsOpen = false;
            return;
        }

        int current = ++version;
        var candidates = await search.SearchAsync(query, CancellationToken.None);
        if (current != version) return; // a newer keystroke superseded this response
        results.ItemsSource = candidates;
        popup.IsOpen = candidates.Count > 0;
    };
    box.TextChanged += (_, _) => debounce.Stop(); // restart the debounce window
    box.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
    results.SelectionChanged += (_, _) =>
    {
        if (results.SelectedItem is GeocodeCandidate picked)
        {
            popup.IsOpen = false;
            callbacks.CommitLocationPick?.Invoke(picked);
        }
    };
    box.KeyDown += (_, e) =>
    {
        if (e.Key == Key.Enter)
        {
            // Commit the typed text (the ambiguity gate decides the fetch).
            callbacks.ApplyInspectorPropertyValue(desc.Property, box.Text);
            popup.IsOpen = false;
        }
    };

    return new StackPanel { Children = { box, popup } };
}
```

(The two `TextChanged` handlers above are written deliberately to show the debounce restart; collapse to one handler in the implementation.)

- [ ] **Step 6: Wire `CommitLocationPick` in MainWindow**

In `MainWindow.xaml.cs`, at the `_inspector` construction site (the `InspectorController` ctor call ~line 171-188), add:

```csharp
commitLocationPick: candidate =>
{
    if (_selectedWidget?.ActiveInstance is IWidgetLocationSearch search)
    {
        search.CommitPick(candidate);
    }
},
```

- [ ] **Step 7: Run all new + inspector tests**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~InspectorEditorProviderTests|FullyQualifiedName~InspectorPanelRendererTests"`
Expected: PASS. Fix the renderer test's pick-hook as needed (the implementation must expose a testable commit path — either via `results.SelectionChanged` driving `CommitLocationPick` directly, or an internal pick delegate).

- [ ] **Step 8: Commit**

```bash
git add ModernWigiDash.Widgets/IWidgetEditorProvider.cs ModernWigiDash.Widgets/WeatherForecastWidget.cs ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs ModernWigiDash.App/Inspector/InspectorController.cs ModernWigiDash.App/MainWindow.xaml.cs ModernWigiDash.Tests/InspectorEditorProviderTests.cs ModernWigiDash.Tests/InspectorPanelRendererTests.cs
git commit -m "feat: search-as-you-type location editor in the inspector"
```

---

### Task 5: Full verification + spec cleanup

**Files:**
- `docs/superpowers/specs/2026-08-14-weather-location-search-design.md` (no change expected)
- Full suite run

- [ ] **Step 1: Full test suite**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: all tests pass (baseline 1065 + this feature's additions).

- [ ] **Step 2: Verify no warnings added**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: no new CS8019/Sonar warnings in the touched files.

- [ ] **Step 3: Physical-device smoke**

Launch the app non-elevated; in the Weather widget's inspector: type "Berl" → the results list appears with the four Berlins; pick "Berlin, New Hampshire, United States" → the widget header shows it and the weather fetches for 44.47/-71.19. Type "Berlin" without picking → the widget shows "Berlin — select which one", no weather. Verify via `%LOCALAPPDATA%\ModernWigiDash\display_device.log` (`[WEATHER]` lines) and the widget's cache file.

- [ ] **Step 4: Commit any smoke-driven adjustments**

```bash
git add -A
git commit -m "fix: smoke-pass adjustments"
```
