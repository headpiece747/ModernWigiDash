# Weather Location UX Revision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Revise the shipped weather location search per on-device feedback: the name is the location (picks set only `Location`; Lat/Lon stays manual), the Location field shows the resolved label with population, and ambiguous typed names silently keep the current weather (no prompt state).

**Architecture:** Three targeted changes to the shipped feature: (1) multi-component suffix resolution in `WeatherClient` so full labels round-trip deterministically; (2) widget cleanup — `CommitPick` writes only `Location`, the select-which-one render state is deleted, and a suppression-flagged resolved-label write-back keeps the field truthful without a fetch loop; (3) the editor seeds from `Location` + the last-resolved population via a new `IWidgetLocationSearch.CurrentPopulation` member.

**Tech Stack:** .NET 10 / C# 14, WPF inspector, Open-Meteo geocoding API, MSTest. House rules: one type per file, the `SetProperty`/`PersistProperty` invariant, capability contracts only in the inspector, MSTest naming.

## Global Constraints

- The **name is the truth**: a pick writes only `Location` (the full label). Latitude/Longitude are filled only by manual entry — never by a pick.
- Population is **display-only**: never persisted; shown as a " · N" suffix in the Location field.
- Ambiguous typed names are **silent**: no fetch, no prompt, no state change — the widget keeps the last good snapshot until a pick resolves.
- Resolved-label write-back writes only **when the label differs**, under a suppression flag so `OnPropertyChanged` does not re-fire a fetch (converges after one extra resolution).
- Multi-component suffixes: **every** component must match a candidate (admin1 / country / country_code, equals 500 / starts-with 250 per component); a non-matching component scores 0.
- All widget-property mutations route through `ModernWidgetBase.SetProperty` — never direct field writes.
- Test command (temp output required): `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`

## File Structure

- `ModernWigiDash.Widgets/WeatherClient.cs` — multi-component `ScoreSuffixMatch`; `LastResolvedPopulation` exposure.
- `ModernWigiDash.Widgets/WeatherForecastWidget.cs` — `CommitPick` shrinks; prompt state deleted; resolved-label write-back.
- `ModernWigiDash.Widgets/IWidgetLocationSearch.cs` — `double? CurrentPopulation` member.
- `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs` — box seeds from label + population suffix.
- Tests: `WeatherClientTests.cs`, `WeatherForecastWidgetTests.cs`, `InspectorPanelRendererTests.cs` (additions/edits).

---

### Task 1: Multi-component suffix resolution

**Files:**
- Modify: `ModernWigiDash.Widgets/WeatherClient.cs` (`ScoreSuffixMatch`, ~line 640)
- Test: `ModernWigiDash.Tests/WeatherClientTests.cs`

**Interfaces:**
- Produces: `ScoreSuffixMatch(string admin1, string country, string code, string? suffixPart)` returns 0 when any comma component fails to match; per-component equals → 500, starts-with → 250, summed.
- Consumes: the existing `SampleBerlines` / `SampleSameNameMultiCountry` fixtures (internal).

- [ ] **Step 1: Write the failing tests**

Add to `WeatherClientTests.cs`:

```csharp
[TestMethod]
public async Task FetchCurrentAsync_FullLabelSuffix_PicksTheUniquePlace()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    // The full label "Berlin, New Hampshire, United States" (what a pick
    // persists) must resolve deterministically: both suffix components match
    // Berlin NH only — the population tiebreak must not come into play.
    var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire, United States", null, null, null));

    Assert.IsNotNull(snapshot);
    Assert.AreEqual(44.46867, snapshot.Lat, 0.0001);
    Assert.AreEqual(-71.18508, snapshot.Lon, 0.0001);
    Assert.IsFalse(client.LastResolutionAmbiguous);
}

[TestMethod]
public async Task FetchCurrentAsync_TwoPartStateAndCountryLabel_MatchesAdmin1AndCountry()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire, United States", null, null, null));

    Assert.IsNotNull(snapshot);
    Assert.AreEqual(44.46867, snapshot.Lat, 0.0001, "admin1 'New Hampshire' and country 'United States' must both match");
}

[TestMethod]
public async Task FetchCurrentAsync_LabelWithNonMatchingComponent_DoesNotResolveToIt()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    // "Berlin, Ontario, United States": no candidate has admin1/country
    // "Ontario" — every component must match, so no suffix score; the bare
    // name tie then flags ambiguity (no fetch).
    var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, Ontario, United States", null, null, null));

    Assert.IsNull(snapshot, "a non-matching suffix component must not resolve to a population pick");
    Assert.IsTrue(client.LastResolutionAmbiguous);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~FullLabelSuffix|FullyQualifiedName~TwoPartStateAndCountry|FullyQualifiedName~LabelWithNonMatchingComponent"`
Expected: FAIL — the multi-component suffix does not match (the old rule compares the whole suffixPart).

- [ ] **Step 3: Implement**

Replace `ScoreSuffixMatch` in `WeatherClient.cs`:

```csharp
private static int ScoreSuffixMatch(string admin1, string country, string code, string? suffixPart)
{
    if (string.IsNullOrWhiteSpace(suffixPart)) return 0;

    // A full label suffix ("New Hampshire, United States" — what a pick
    // persists) must match component by component: every component must hit
    // admin1/country/code, else the place does not match the label at all
    // (the population tiebreak must never re-pick a wrong city from a
    // persisted label).
    string[] components = suffixPart.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    int score = 0;
    foreach (string component in components)
    {
        if (EqualsAny(admin1, country, code, component)) score += 500;
        else if (StartsWithAny(admin1, country, code, component)) score += 250;
        else return 0;
    }
    return score;
}

private static bool EqualsAny(string admin1, string country, string code, string component)
    => admin1.Equals(component, StringComparison.OrdinalIgnoreCase)
        || country.Equals(component, StringComparison.OrdinalIgnoreCase)
        || code.Equals(component, StringComparison.OrdinalIgnoreCase);

private static bool StartsWithAny(string admin1, string country, string code, string component)
    => admin1.StartsWith(component, StringComparison.OrdinalIgnoreCase)
        || country.StartsWith(component, StringComparison.OrdinalIgnoreCase)
        || code.StartsWith(component, StringComparison.OrdinalIgnoreCase);
```

Single-component behavior is preserved: "Springfield, MA" → "Massachusetts".StartsWith("MA") → 250 (same as before); "Berlin, New Hampshire" → equals → 500.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 filter, then the full `WeatherClientTests` filter.
Expected: PASS (3 new + all existing geocode/gate tests).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/WeatherClient.cs ModernWigiDash.Tests/WeatherClientTests.cs
git commit -m "feat: multi-component suffix resolution — persisted location labels round-trip deterministically"
```

---

### Task 2: Widget — pick writes only Location; prompt state deleted; resolved-label write-back

**Files:**
- Modify: `ModernWigiDash.Widgets/WeatherForecastWidget.cs`
- Modify: `ModernWigiDash.Tests/WeatherForecastWidgetTests.cs`

**Interfaces:**
- Consumes: `WeatherClient.LastResolutionAmbiguous` (stays), `WeatherSnapshot.ResolvedCityName`.
- Produces: `CommitPick(GeocodeCandidate)` writes ONLY `Location` = `candidate.Label` via `SetProperty`.
- Produces: `internal bool _suppressLocationWriteback` — the OnPropertyChanged guard for the resolved-label write-back.

- [ ] **Step 1: Write the failing tests**

In `WeatherForecastWidgetTests.cs`, replace the shipped `CommitPick_SetsLocationLatLonAndClearsLocationMatch` test and add:

```csharp
[TestMethod]
public void CommitPick_SetsOnlyLocation()
{
    var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())) };
    var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

    var search = (IWidgetLocationSearch)widget;
    search.CommitPick(new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508));

    Assert.AreEqual("Berlin, New Hampshire, United States", widget.Location);
    Assert.AreEqual("", widget.Latitude, "Lat/Lon must stay manual-only — never filled by a pick");
    Assert.AreEqual("", widget.Longitude);
    Assert.IsTrue(placed.PropertyValues.ContainsKey("Location"), "the pick must persist the label");
    Assert.IsFalse(placed.PropertyValues.ContainsKey("Latitude"), "no Lat/Lon may be persisted by a pick");
}

[TestMethod]
public void FetchLiveWeatherAsync_AmbiguousName_KeepsStateSilently()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return StubHttpHandler.NotFound();
    });
    var widget = new WeatherForecastWidget { Location = "Berlin" };
    widget.TestHttpClient = new HttpClient(stub);
    var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
    var renders = context.Renders;

    // The shipped prompt state is gone: an ambiguous resolution must be
    // silent — no fetch, no state change, no render request.
    // (Drive the fetch directly; the client's ambiguity flag suppresses it.)
    widget._suppressLocationWriteback = true; // isolate this test from the write-back path
    await widget.FetchLiveWeatherAsync();

    Assert.AreEqual(renders, context.Renders, "no render request for an ambiguous resolution");
    Assert.AreEqual(0, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
        "no forecast request for an ambiguous name — the gate is silent");
}
```

> **Note (test shaping):** the "silent" test asserts the observable surface — no
> forecast request (the stub counts calls) and no render request. `_needsLocationSelection`
> is deleted in this task; no residual state field may remain.

```csharp
[TestMethod]
public void FetchLiveWeatherAsync_ResolvedLabel_IsWrittenBackToLocation()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var widget = new WeatherForecastWidget { Location = "Victoria" };
    widget.TestHttpClient = new HttpClient(stub);
    var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

    await widget.FetchLiveWeatherAsync();

    Assert.AreEqual("Victoria, British Columbia, Canada", widget.Location,
        "a successful resolution must write the resolved label back into Location");
    Assert.IsTrue(placed.PropertyValues.ContainsKey("Location"));
    Assert.AreEqual(1, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
        "the write-back must not loop: exactly one forecast request");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~CommitPick_SetsOnlyLocation|FullyQualifiedName~AmbiguousName_KeepsStateSilently|FullyQualifiedName~ResolvedLabel_IsWrittenBackToLocation"`
Expected: FAIL — CommitPick still writes Lat/Lon; the prompt state still exists; no write-back.

- [ ] **Step 3: Implement**

In `WeatherForecastWidget.cs`:

1. `CommitPick` becomes:

```csharp
public void CommitPick(GeocodeCandidate candidate)
{
    // The name is the truth: a pick writes only the label. Latitude/Longitude
    // stay manual-only — the label resolves deterministically (multi-component
    // suffix matching).
    SetProperty(nameof(Location), candidate.Label);
}
```

2. Delete the prompt state: remove the `_needsLocationSelection` field, the `Render` prompt block, and the `FetchLiveWeatherAsync` ambiguity-flag branch (an ambiguous null snapshot now just returns — the client's flag already suppressed the fetch).

3. Add the write-back with the suppression guard:

```csharp
/// <summary>Suppresses the OnPropertyChanged fetch while the resolved label
/// is written back after a successful resolution (the write-back must not
/// re-fire a fetch).</summary>
internal bool _suppressLocationWriteback;

// in FetchLiveWeatherAsync, after ApplySnapshot(snapshot) succeeds:
if (!string.IsNullOrWhiteSpace(snapshot.ResolvedCityName)
    && snapshot.ResolvedCityName != Location)
{
    _suppressLocationWriteback = true;
    try
    {
        SetProperty(nameof(Location), snapshot.ResolvedCityName);
    }
    finally
    {
        _suppressLocationWriteback = false;
    }
}
```

4. In `OnPropertyChanged`, the `Location`/`Latitude`/`Longitude`/`CountryCode` branch: skip the forced fetch while `_suppressLocationWriteback` (return after `base.OnPropertyChanged`), so the write-back converges without a loop:

```csharp
else if (propertyName is nameof(Location) or nameof(Latitude) or nameof(Longitude) or nameof(CountryCode))
{
    if (!_suppressLocationWriteback)
    {
        _client.InvalidateLocation();
        RequestRefresh(force: true);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 filter, then the full `WeatherForecastWidgetTests` filter.
Expected: PASS. (The write-back test asserts one fetch — the initial resolution — and the label write without a second fetch.)

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/WeatherForecastWidget.cs ModernWigiDash.Tests/WeatherForecastWidgetTests.cs
git commit -m "feat: name is the location — pick writes only Location; silent ambiguity; resolved-label write-back"
```

---

### Task 3: Editor — Location field shows label + population

**Files:**
- Modify: `ModernWigiDash.Widgets/IWidgetLocationSearch.cs`
- Modify: `ModernWigiDash.Widgets/WeatherClient.cs` (expose last-resolved population)
- Modify: `ModernWigiDash.Widgets/WeatherForecastWidget.cs` (CurrentPopulation)
- Modify: `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs` (box seed)
- Test: `ModernWigiDash.Tests/InspectorPanelRendererTests.cs`, `ModernWigiDash.Tests/WeatherClientTests.cs`

**Interfaces:**
- Produces: `WeatherClient.LastResolvedPopulation` — `internal double` (the winner's population; 0 when unknown, e.g. ZIP resolution).
- Produces: `IWidgetLocationSearch.CurrentPopulation` — `double?` (null when none resolved; delegates to the client).
- Consumes: Task 2's `CommitPick` (Location-only).

- [ ] **Step 1: Write the failing tests**

In `WeatherClientTests.cs`:

```csharp
[TestMethod]
public async Task FetchCurrentAsync_ResolvedWinner_ExposesPopulation()
{
    var stub = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var client = CreateClient(stub);

    var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire, United States", null, null, null));

    Assert.IsNotNull(snapshot);
    Assert.AreEqual(9367, client.LastResolvedPopulation, 0.0001);
}
```

In `InspectorPanelRendererTests.cs` (extend the existing search-editor test; the Location row's TextBox is the only TextBox the weather widget's custom-properties panel builds — the same `FindVisualChildren<TextBox>` pattern the shipped editor test uses for `Popup`):

```csharp
[TestMethod]
public void Render_LocationSearchWidget_SeedsBoxWithLabelAndPopulation()
{
    // Seed the client's last-resolved population through the widget's client
    // seam: resolving the Berlin NH label sets LastResolvedPopulation = 9367.
    var resolver = new StubHttpHandler(request =>
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
        return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
    });
    var widget = new WeatherForecastWidget { Location = "Berlin, New Hampshire, United States" };
    widget.TestHttpClient = new HttpClient(resolver);
    var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
    var profile = new ProfileLayout();
    profile.ActivePage.Widgets.Add(placed);
    var context = new PersistingContext(profile);
    widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
    widget._suppressLocationWriteback = true;
    widget.FetchLiveWeatherAsync().GetAwaiter().GetResult();
    Assert.AreEqual(9367, widget.CurrentPopulation, "precondition: the resolution must expose the population");

    var target = new StackPanel();
    var callbacks = new InspectorCallbacks
    {
        TryFindResource = _ => null,
        ApplyInspectorPropertyValue = (_, _) => { },
        ShowIconSelectorPopup = null,
        AttachDropdownWithinWindow = null,
        CommitLocationPick = _ => { },
    };

    InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed), target.Children, () => false, callbacks);

    var box = FindVisualChildren<TextBox>(target).First();
    Assert.IsTrue(box.Text.Contains("Berlin, New Hampshire, United States"), "the box must seed from the Location label");
    Assert.IsTrue(box.Text.Contains("9k"), "the box must append the population suffix from CurrentPopulation");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ExposesPopulation|FullyQualifiedName~SeedsBoxWithLabelAndPopulation"`
Expected: FAIL — no population exposure, no suffix in the box.

- [ ] **Step 3: Implement**

1. `WeatherClient.cs` — add the property; set it where the winner is chosen in `GeocodeCityLocationAsync` (the unique top scorer's `Population`, via the ranked tuple) and where a LocationMatch pick promotes (the picked candidate's population); reset to 0 on a tie and in the ZIP path:

```csharp
/// <summary>The last resolved winner's population (0 when the resolution had
/// no population, e.g. ZIP/coordinate paths or an ambiguous tie).</summary>
internal double LastResolvedPopulation { get; private set; }
```

2. `IWidgetLocationSearch.cs` — add:

```csharp
/// <summary>The last resolved location's population (null when none resolved)
/// — the editor's display suffix; never persisted.</summary>
double? CurrentPopulation { get; }
```

3. `WeatherForecastWidget.cs` — implement:

```csharp
public double? CurrentPopulation => _client.LastResolvedPopulation > 0 ? _client.LastResolvedPopulation : null;
```

4. `InspectorPanelRenderer.cs` — seed the search box in `BuildLocationSearchEditor`:

```csharp
string seed = desc.CurrentValue?.ToString() ?? "";
if (search.CurrentPopulation is > 0)
{
    seed = $"{seed} · {search.CurrentPopulation.Value:0.#}";
}
var box = new TextBox { Text = seed };
```

(Format: "8.4M" style is achieved with `0.#` + a magnitude suffix helper for thousands/millions — implement a small `FormatPopulation(double)` static: `< 1000 → "N"`, `< 1e6 → "Nk"`, else "NM" with one decimal, matching the search list's own label formatting. If the search list's converter already formats population, reuse the same formatting.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 filter, then `--filter "FullyQualifiedName~InspectorPanelRendererTests"`, then the full suite.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/IWidgetLocationSearch.cs ModernWigiDash.Widgets/WeatherClient.cs ModernWigiDash.Widgets/WeatherForecastWidget.cs ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs ModernWigiDash.Tests/WeatherClientTests.cs ModernWigiDash.Tests/InspectorPanelRendererTests.cs
git commit -m "feat: location field shows the resolved label with population"
```

---

### Task 4: Full verification + device smoke

**Files:**
- Full suite + build warnings
- Device run

- [ ] **Step 1: Full test suite**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: all tests pass (baseline 1084 + this revision's additions).

- [ ] **Step 2: Verify no warnings added**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: 0 warnings.

- [ ] **Step 3: Device smoke**

Launch the app non-elevated; on the Weather widget's inspector:
1. The Location field shows "New York, New York, United States · 8.4M" (or the configured place) with Lat/Lon empty.
2. Type "Berl" → the labeled list drops; pick "Berlin, New Hampshire, United States" → the field shows that label (+population), Lat/Lon stay empty, the widget fetches Berlin NH's weather.
3. Type "Berlin" without picking → the widget keeps the current weather (no prompt, no fetch).
4. Restart the app → the field still shows the picked label and the same weather (label round-trip).
Verify via `%LOCALAPPDATA%\ModernWigiDash\display_device.log` and the widget's cache.

- [ ] **Step 4: Commit any smoke-driven adjustments**

```bash
git add -A
git commit -m "fix: smoke-pass adjustments"
```
