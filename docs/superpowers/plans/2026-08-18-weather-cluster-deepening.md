# Weather Cluster Deepening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute the 2026-08-18 architecture review's three cards against the weather forecast widget cluster: give the stale-identity invariant a single owner (Card 1), pin the render-model identity key (Card 3), and collapse the geocoder's two doors into one (Card 2).

**Source:** `docs/reports/architecture-review-20260818-114407.html` — the repo copy is canonical (it was captured from `C:\Users\tobia\AppData\Local\Temp\opencode\architecture-review-20260818-114407.html`; the Temp copy is not durable). Baseline commit: `3b62ce8`.

**Architecture:** The review mapped the cluster as Client / Fetch control / Cache store / Geocoder / Location resolver / Render model / Renderer / Presentation, with red "leak" edges exactly where the same rule is spelled a second time. Three cards, in the review's own order:

1. **Card 1 — One owner of the stale-identity invariant** (`#card-1`, "Worth exploring", *start here*). The stale-identity rule is spelled in 4-plus ways — the client's capture window, the widget's return→apply recheck, the cache-load comparison — held together by comments with no test surface. Solution: a single predicate over a single identity record, owned by one module and named in a new ADR.
2. **Card 3 — Pin the identity rules of the render model** (`#card-3`, "Worth exploring", lands as a same-session quick win on top of Card 1). `WeatherRenderModelKey` (data version + bounds + property snapshot) — the rule deciding when the cached model rebuilds — has no covering test.
3. **Card 2 — The geocoder's two doors** (`#card-2`, "Speculative", last). The client reaches the location resolver through two doors: the geocoder's geocode entry point AND three direct leaf calls (is-ZIP, zip-lookup URI, forecast URI). Solution: a single geocode-entry seam through the geocoder, with the leaf calls absorbed behind it.

The review also names what is **kept as-is** — the deletion test passed for these; do not touch them:

- **Renderer** — a pure pixels-out adapter over the render model; deleting it would not make the module interface shallower.
- **Presentation** — display-string rules are pure with one consumer; the seam is hypothetical.
- **Fetch control** — deep and load-bearing; Card 1 touches its *stamp*, not the module.
- **Forecast limits** — pure constants with the two-layer cap invariant stated once.

**Tech Stack:** .NET 10 / C# 14, WPF widgets, Open-Meteo + zippopotam geocoding, MSTest. House rules: one type per file, `internal` by default, MSTest naming (`Method_Scenario_Expected`/`ExpectedResult`), no mocking frameworks for things we own.

## Global Constraints

- **Retained pins — never weaken existing assertions.** Migrations change which module spells the rule, never what is asserted. The exact retained sites are listed per task.
- **Execution order is the review's:** Card 1 → Card 3 (quick win) → Card 2 (speculative, last). Card 1 shrinks the client's surface — the very surface Card 2 then targets.
- **Kept modules stay untouched:** Renderer, Presentation, Fetch control, Forecast limits (reasons above).
- **Commit style:** `type(scope): imperative summary`, one logical change per commit; feature and its tests belong together.
- **Test command** (temp output required when the app may be running):
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- **Grep gate after each refactor:** no stale spelling of a retired rule may remain in the solution (each task names its gate).

## File Structure

- `ModernWigiDash.Widgets/WeatherQueryKey.cs` — **new**, the identity owner (Card 1).
- `ModernWigiDash.Tests/WeatherQueryKeyTests.cs` — **new**, the identity-guard test surface (Card 1 RED).
- `ModernWigiDash.Widgets/WeatherClient.cs` — key construction + three guard sites move; `BuildQueryKey`/`EscapeKeyField` deleted.
- `ModernWigiDash.Widgets/WeatherForecastWidget.cs` — `fetchKey` / `StillCurrent` / `locationKeyBefore` sites move.
- `ModernWigiDash.Widgets/WeatherResolvedIdentity.cs` — `ResolutionInvalidationProperties` becomes an alias of the module's set.
- `ModernWigiDash.Tests/WeatherClientTests.cs` — four `BuildQueryKey` references migrate (assertions verbatim).
- `ModernWigiDash.Tests/WeatherRenderModelTests.cs` — **new** (Card 3).
- `ModernWigiDash.Widgets/WeatherGeocoder.cs`, `ModernWigiDash.Widgets/WeatherLocationResolver.cs` — Card 2 single-door consolidation.
- `docs/adr/0006-stale-identity-invariant.md` — **new ADR** (Card 1's decision record).
- `CONTEXT.md` — `WeatherQueryKey` glossary entry + ADR-0006 row.

---

### Task 1: `WeatherQueryKey` — one owner of the stale-identity invariant (Card 1)

**Files:**
- Create: `ModernWigiDash.Widgets/WeatherQueryKey.cs`
- Create (RED): `ModernWigiDash.Tests/WeatherQueryKeyTests.cs`
- Modify: `ModernWigiDash.Widgets/WeatherClient.cs` (call sites L168, L294, L309; delete `BuildQueryKey` L453–466)
- Modify: `ModernWigiDash.Widgets/WeatherForecastWidget.cs` (call sites L432, L591, L642 + downstream `locationKeyBefore` compare — find by reading L650–710)
- Modify: `ModernWigiDash.Widgets/WeatherResolvedIdentity.cs` (L87–95 alias)
- Modify: `ModernWigiDash.Tests/WeatherClientTests.cs` (L798, L857, L897, L931)

**Interfaces:**
- Produces: `internal static class WeatherQueryKey` with
  - `string Build(WeatherLocation)` — the ONLY key construction;
  - `bool SameKey(string? left, string? right)` — the ONLY identity predicate (ordinal, null-safe);
  - `string[] KeyPropertyNames` (6 fields, key order);
  - `string[] InvalidationProperties` (5 — every key field except `LocationMatch`);
  - `string LocationMatchProperty` (`"LocationMatch"`).
- Consumes: the existing `WeatherLocation` record (unchanged).

**Identity rule (the module's contract, spelled once):**
- Key = `'LocationType|Location|Latitude|Longitude|CountryCode|LocationMatch'`; a null field contributes an empty segment.
- Escape `\` → `\\` and `|` → `\|` inside each field, so a separator character inside a field can never forge a colliding key.
- `CustomLabel` is NOT in the key — a label edit must not re-fetch.
- Comparison is ordinal: case is identity, so a case change counts as a new place.

- [ ] **Step 1 (RED): Write the failing tests** — create `ModernWigiDash.Tests/WeatherQueryKeyTests.cs`:

```csharp
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherQueryKeyTests
{
    private static WeatherLocation MakeLocation(string type = "Fixed Location", string location = "Berlin",
        string? lat = null, string? lon = null, string? country = null, string? match = null,
        string? customLabel = null)
        => new(type, location, lat, lon, customLabel) { CountryCode = country, LocationMatch = match };

    [TestMethod]
    public void Build_AllFieldsPresent_JoinsTheSixKeyFieldsInOrder()
    {
        var key = WeatherQueryKey.Build(MakeLocation("City", "Berlin", "52.5", "13.4", "DE", "Exact", "Home"));

        Assert.AreEqual("City|Berlin|52.5|13.4|DE|Exact", key);
    }

    [TestMethod]
    public void Build_NullOptionalFields_YieldEmptySegments()
    {
        var key = WeatherQueryKey.Build(MakeLocation("City", "Berlin"));

        Assert.AreEqual("City|Berlin||||", key);
    }

    [TestMethod]
    public void Build_FieldContainingSeparatorOrBackslash_CannotForgeACollidingKey()
    {
        // Unescaped, the first location ("a|b", no lat) would join to the
        // identical string as the second ("a", lat "b") — a separator inside
        // a field readable as a field boundary.
        var separatorField = WeatherQueryKey.Build(MakeLocation("City", "a|b"));
        var twoFields = WeatherQueryKey.Build(MakeLocation("City", "a", lat: "b"));
        var backslash = WeatherQueryKey.Build(MakeLocation("City", "a\\b"));

        Assert.AreNotEqual(separatorField, twoFields);
        Assert.IsTrue(separatorField.Contains("\\|", StringComparison.Ordinal), "a field's '|' must be escaped");
        Assert.IsTrue(backslash.Contains("\\\\", StringComparison.Ordinal), "a field's '\\' must be escaped");
    }

    [TestMethod]
    public void Build_CustomLabelChange_KeepsTheSameKey()
    {
        // A label edit must not re-fetch: CustomLabel is display-only.
        var a = WeatherQueryKey.Build(MakeLocation("City", "Berlin", customLabel: "Home"));
        var b = WeatherQueryKey.Build(MakeLocation("City", "Berlin", customLabel: "Work"));

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Build_EachIdentityFieldChange_YieldsADifferentKey()
    {
        // Derived from the RECORD (the source of truth), not the module's own
        // constant: a new resolution input added to WeatherLocation fails
        // this test until it is in the key.
        var baseline = WeatherQueryKey.Build(MakeLocation());
        foreach (var property in typeof(WeatherLocation).GetProperties())
        {
            if (string.Equals(property.Name, nameof(WeatherLocation.CustomLabel), StringComparison.Ordinal)) continue;

            WeatherLocation changed = property.Name switch
            {
                nameof(WeatherLocation.LocationType) => MakeLocation(type: "Other"),
                nameof(WeatherLocation.Location) => MakeLocation(location: "Paris"),
                nameof(WeatherLocation.Latitude) => MakeLocation(lat: "1.0"),
                nameof(WeatherLocation.Longitude) => MakeLocation(lon: "2.0"),
                nameof(WeatherLocation.CountryCode) => MakeLocation(country: "FR"),
                nameof(WeatherLocation.LocationMatch) => MakeLocation(match: "Different"),
                _ => throw new NotSupportedException(property.Name),
            };

            Assert.AreNotEqual(baseline, WeatherQueryKey.Build(changed), $"changing {property.Name} must change the key");
        }
    }

    [TestMethod]
    public void SameKey_ComparesOrdinalAndCaseSensitive()
    {
        var key = WeatherQueryKey.Build(MakeLocation());

        Assert.IsTrue(WeatherQueryKey.SameKey(key, key));
        Assert.IsFalse(WeatherQueryKey.SameKey(key, key.ToUpperInvariant()));
        Assert.IsFalse(WeatherQueryKey.SameKey(null, key));
    }

    [TestMethod]
    public void KeyPropertyNames_CoverEveryResolutionInputExceptCustomLabel()
    {
        var recordFields = typeof(WeatherLocation).GetProperties()
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, nameof(WeatherLocation.CustomLabel), StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(recordFields, WeatherQueryKey.KeyPropertyNames,
            "the key must cover every resolution input exactly once");
    }

    [TestMethod]
    public void InvalidationProperties_PlusLocationMatch_AreExactlyTheKeyFields()
    {
        // The re-fetch set + LocationMatch's own invalidation branch must
        // cover the key fields exactly — an input that neither re-fetches
        // nor invalidates would change the identity silently.
        var guardSet = WeatherQueryKey.InvalidationProperties
            .Append(WeatherQueryKey.LocationMatchProperty)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var keySet = WeatherQueryKey.KeyPropertyNames
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(keySet, guardSet);
    }
}
```

- [ ] **Step 2 (RED): Confirm the valid failure** — run the filtered test:
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter 'FullyQualifiedName~WeatherQueryKeyTests'`
  Failure mode: the test project does not compile (`WeatherQueryKey` does not exist) — a valid RED for an extraction.

- [ ] **Step 3 (GREEN): Create the module** — `ModernWigiDash.Widgets/WeatherQueryKey.cs`:

```csharp
namespace ModernWigiDash.Widgets;

/// <summary>
/// One owner of the weather cluster's resolution identity (ADR-0006):
/// the query key naming "which place was this weather resolved for".
/// The client's capture window, the cache-load identity stamp, and the
/// widget's return-to-apply recheck all route through <see cref="Build"/>
/// and <see cref="SameKey"/> — one spelling of the identity, one ordinal
/// predicate, no second rule held in comments.
/// </summary>
internal static class WeatherQueryKey
{
    /// <summary>The one field whose change invalidates the resolved
    /// name/population through its own branch (the widget's
    /// InvalidateCoordinates) instead of the full re-fetch set — while
    /// still turning the key.</summary>
    internal const string LocationMatchProperty = nameof(WeatherLocation.LocationMatch);

    /// <summary>The six identity fields in key order: a change in any one
    /// is an identity change (a re-fetch). WeatherLocation.CustomLabel is
    /// deliberately absent — a label edit must not re-fetch.</summary>
    internal static readonly string[] KeyPropertyNames =
    [
        nameof(WeatherLocation.LocationType),
        nameof(WeatherLocation.Location),
        nameof(WeatherLocation.Latitude),
        nameof(WeatherLocation.Longitude),
        nameof(WeatherLocation.CountryCode),
        LocationMatchProperty,
    ];

    /// <summary>The five resolution inputs that force a re-fetch on change
    /// — every key field except <see cref="LocationMatchProperty"/>, which
    /// has its own invalidation branch.
    /// <see cref="WeatherResolvedIdentity.ResolutionInvalidationProperties"/>
    /// aliases this set, so the widget's drift test pins it to the record.</summary>
    internal static readonly string[] InvalidationProperties =
    [
        nameof(WeatherLocation.Location),
        nameof(WeatherLocation.Latitude),
        nameof(WeatherLocation.Longitude),
        nameof(WeatherLocation.CountryCode),
        nameof(WeatherLocation.LocationType),
    ];

    /// <summary>
    /// The resolution identity key — one spelling for the client's per-query
    /// geocode cache, the cache-load identity stamp, and the widget's
    /// in-flight staleness guard: a change in any resolution input yields a
    /// different key. Fields are backslash-escaped and joined with '|' so a
    /// separator character inside a field can never forge a colliding key.
    /// </summary>
    internal static string Build(WeatherLocation location)
        => string.Join('|',
            EscapeKeyField(location.LocationType), EscapeKeyField(location.Location),
            EscapeKeyField(location.Latitude), EscapeKeyField(location.Longitude),
            EscapeKeyField(location.CountryCode), EscapeKeyField(location.LocationMatch));

    /// <summary>The single identity predicate: ordinal comparison — case is
    /// identity, so a case change is a new place. Null-safe on both sides.</summary>
    internal static bool SameKey(string? left, string? right)
        => string.Equals(left, right, StringComparison.Ordinal);

    private static string EscapeKeyField(string? value)
        => (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|");
}
```

- [ ] **Step 4 (GREEN): Confirm green** — rerun the filtered test; all `WeatherQueryKeyTests` pass.

- [ ] **Step 5 (REFACTOR): Migrate every guard site to the owner.** Exact edits:
  1. `WeatherClient.cs` L168 — `string fetchQueryKey = BuildQueryKey(location);` → `string fetchQueryKey = WeatherQueryKey.Build(location);`
  2. `WeatherClient.cs` L293–294 — the cache-load stamp compare becomes
     `if (!string.IsNullOrEmpty(payload.LocationQueryKey) && !WeatherQueryKey.SameKey(payload.LocationQueryKey, WeatherQueryKey.Build(location)))`.
     The empty-stamp (legacy cache) trust must be preserved verbatim.
  3. `WeatherClient.cs` L309 — `TryApplyCacheIdentity(BuildQueryKey(location), ...)` → `TryApplyCacheIdentity(WeatherQueryKey.Build(location), ...)`.
  4. `WeatherClient.cs` L450 — `_fetchControl.Stamp(fetchQueryKey);` unchanged (the local is already built at L168).
  5. `WeatherForecastWidget.cs` L432 — `string fetchKey = WeatherClient.BuildQueryKey(BuildLocation());` → `string fetchKey = WeatherQueryKey.Build(BuildLocation());`
  6. `WeatherForecastWidget.cs` L591 (`StillCurrent`) — `=> string.Equals(key, WeatherClient.BuildQueryKey(BuildLocation()), StringComparison.Ordinal);` → `=> WeatherQueryKey.SameKey(key, WeatherQueryKey.Build(BuildLocation()));`
  7. `WeatherForecastWidget.cs` L642 — `locationKeyBefore = WeatherClient.BuildQueryKey(BuildLocation());` → `locationKeyBefore = WeatherQueryKey.Build(BuildLocation());` — and the downstream `locationKeyBefore` comparison further down in `LoadCachedWeatherAsync` (read L650–710 to find it) converts to `WeatherQueryKey.SameKey(...)`.
  8. `WeatherClientTests.cs` L798, L857, L897, L931 — `WeatherClient.BuildQueryKey(x)` → `WeatherQueryKey.Build(x)`; the assertions themselves stay verbatim (retained pins: L797–799 fetched-query-key assert, L856–858 / L896–898 / L930–932 stale-query-key asserts).
  9. `WeatherResolvedIdentity.cs` L87–95 — `ResolutionInvalidationProperties` becomes an alias: `internal static readonly string[] ResolutionInvalidationProperties = WeatherQueryKey.InvalidationProperties;` and the doc comment retargets from "mirror of `WeatherClient.BuildQueryKey`" to "alias of `WeatherQueryKey.InvalidationProperties` (owner of the set; ADR-0006)". The widget drift pin (`WeatherForecastWidgetTests.OnPropertyChanged_InvalidationSet_CoversEveryResolutionInput`, L943–967) keeps reading the alias unchanged.
  10. Delete `WeatherClient.BuildQueryKey` + `EscapeKeyField` (L453–466 including the doc comment).
- [ ] **Step 6 (REFACTOR): Grep gate** — `BuildQueryKey` has zero hits in the solution (the owner's members are `Build`/`SameKey`; nothing references the retired spelling).
- [ ] **Step 7 (REFACTOR): Verify** — weather-filtered run green
  (`--filter 'FullyQualifiedName~Weather'`), then the full suite green (command in Global Constraints).
- [ ] **Step 8: Commit** — `refactor(Widgets): extract the weather query-key identity rule into WeatherQueryKey`

### Task 2: Name the invariant — ADR-0006 + CONTEXT.md

**Files:**
- Create: `docs/adr/0006-stale-identity-invariant.md`
- Modify: `CONTEXT.md` (Core Concepts table + ADR table)

- [ ] **Step 1:** Author the ADR in house format (date 2026-08-18, status Accepted, deciders "Project owner"):
  - **Context:** the stale-identity rule ("never apply weather resolved for a different place than the UI shows") was spelled 4+ ways — the client's capture-window re-validation (fetch-start key + post-save re-check), the cache-load identity-stamp compare, and the widget's return→apply re-check (`StillCurrent` + `locationKeyBefore`) — held by comments alone, with no test surface (the 2026-08-18 architecture review, Card 1).
  - **Decision:** the resolution identity has one owner, `ModernWigiDash.Widgets.WeatherQueryKey`: `Build` is the only key construction, `SameKey` the only predicate (ordinal, null-safe), and `KeyPropertyNames` / `InvalidationProperties` / `LocationMatchProperty` declare the identity field sets exactly once. Every guard site routes through it; `WeatherResolvedIdentity.ResolutionInvalidationProperties` aliases the set.
  - **Consequences:** positive — one test surface (`WeatherQueryKeyTests` pins the rule without pixels or fetch mocks); a new resolution input fails the derived record tests until it is wired into the key and the invalidation set. Negative — one more module in the cluster (small: no state, no I/O).
  - **Alternatives considered:** (1) better comments at the guard sites — comments already failed to keep the spellings in sync and still leave no test surface; (2) moving the key into `WeatherFetchControl` — the key is also the cache-stamp identity and the widget's guard input, which would give the fetch control a cross-module concern (the review keeps fetch control deep: it owns the stamp mechanics, not the identity rule); (3) a struct identity record instead of an escaped string — the cache stamp is persisted as a string, so a struct would need stamp serialization; the escaped string key is uniform and forgery-proof.
- [ ] **Step 2:** `CONTEXT.md` — add a `WeatherQueryKey` row to the Core Concepts table (owner of the resolution identity; key format + escaping; the three guard sites that route through it; ADR-0006) and an ADR-0006 row to the Architecture Decisions table.
- [ ] **Step 3: Commit** — `docs: record the weather stale-identity invariant (ADR-0006 + CONTEXT.md)`

### Task 3: Pin the render-model identity key (Card 3)

**Files:**
- Create: `ModernWigiDash.Tests/WeatherRenderModelTests.cs`
- Modify (only if the hit path needs a seam): `ModernWigiDash.Tests/WeatherWidgetRendererTests.cs`

**Interfaces:**
- Consumes: `WeatherRenderModel` / `WeatherRenderModelKey` (data version + bounds + property snapshot) — read the module first to name the exact key fields and the rebuild/decision function before writing tests.
- Produces: a pinning suite over the identity record — no pixels, no fetch mocks.

- [ ] **Step 1:** Read `ModernWigiDash.Widgets/WeatherRenderModel.cs` (the key record + the cached-rebuild check) and the widget's build-once/rebuild-on-drift call site; note the exact key fields and the comparison point.
- [ ] **Step 2 (RED→GREEN in one file for a pure pinning task):** write `WeatherRenderModelTests` — one assertion concept per test:
  - property drift (each property-snapshot field changed alone) → rebuild (key differs);
  - bounds drift (width, then height) → rebuild;
  - data-version drift → rebuild;
  - no drift → cache hit (key equal, verdict re-read, no rebuild);
  - the null-`Key` on a never-built model makes a cache hit unrepresentable without identity (the stated invariant).
  Derive the per-field drift loop from the key record's properties where practical, mirroring the house derived-pin style.
- [ ] **Step 3:** If the hit verdict is untestable without a widget instance, extend `WeatherWidgetRendererTests` with a minimal seam only as wide as needed.
- [ ] **Step 4:** Weather-filtered run green; full suite green.
- [ ] **Step 5: Commit** — `test(Widgets): pin the weather render-model identity key`

### Task 4: One door — the geocoder's two doors (Card 2, speculative)

**Files:**
- Modify: `ModernWigiDash.Widgets/WeatherClient.cs` (the three direct leaf calls leave)
- Modify: `ModernWigiDash.Widgets/WeatherGeocoder.cs` (the single entry; leaf calls absorbed)
- Modify: `ModernWigiDash.Widgets/WeatherLocationResolver.cs` only if a leaf's home must move
- Modify: `ModernWigiDash.Tests/WeatherClientTests.cs` (+ a geocoder-entry test file if the entry is new)

**Interfaces (from the review):**
- Absorbs: the client's three direct leaf calls into the location resolver — the **is-ZIP check**, the **zip-lookup URI** build, and the **forecast URI** build — behind the geocoder's single geocode-entry seam (city (7) / zip (4) entry already exists).
- Produces: one geocode entry the client asks the question of; the client stops building geocoding URLs. The geocoder's entry points are the test surface (assertions over the entries, no HTTP beyond the `StubHttpHandler` seam).
- The decision rules stay in the resolver where they already live — the geocoder is transport + one door, not a second decision site.

- [ ] **Step 1:** Read `WeatherGeocoder.cs` and `WeatherLocationResolver.cs`, and grep the client for the three leaf call sites; name each leaf (method, signature, and where the URI/decision is built today).
- [ ] **Step 2:** Design the single entry (expected shape: one geocoder method taking the location/identity that returns the resolved coordinates plus the forecast-URI question, subsuming is-ZIP + zip-lookup + forecast-URI); confirm the client's city/zip entry and leaf sites all route through it.
- [ ] **Step 3 (RED):** tests over the geocoder entry points (through the HttpClient seam) pinning the absorbed behavior — zip routing, forecast URL shape, the is-ZIP boundary — before the client's leaf calls exist behind the door.
- [ ] **Step 4 (GREEN/REFACTOR):** implement the single door; delete the client's direct leaf calls; the resolver's rule comments keep their homes.
- [ ] **Step 5:** Grep gate — no geocoding-URI construction remains in `WeatherClient`; weather-filtered + full suite green.
- [ ] **Step 6: Commit** — `refactor(Widgets): give the weather geocoder one door`

---

### Final verification

- [ ] Full build: `dotnet build ModernWigiDash.slnx -c Release --nologo`
- [ ] Full test suite green (temp-output command in Global Constraints).
- [ ] **Physical acceptance (user at the machine, elevated run):** the weather widget on the real WigiDash — location search / pick / fetch / render, label edit (no spurious re-fetch), cache reload across restart, .NET 10. No regressions against the pre-change baseline.