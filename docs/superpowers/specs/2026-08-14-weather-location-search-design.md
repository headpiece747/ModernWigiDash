# Weather Location Search — Design

**Status:** Approved (2026-08-14)
**Scope:** Weather widget location entry — search-as-you-type in the inspector
**Related:** `2026-08-12-weather-location-design.md` (geocoding + Location Match)

## Problem

Typing a bare city name like "Berlin" silently resolves to the highest-population
same-named city (Berlin, Germany) and the widget shows that city's weather —
reported on-device as "all temperature data is wrong" (the header did read
"Berlin, State of Berlin, Germany", but that is easy to miss). The existing
**Location Match** dropdown is the right mechanism but requires the user to
discover it after the wrong data is already showing.

**Principle:** wrong data never displays. An ambiguous location is never
silently resolved to a population-decided pick; the user must choose.

## Chosen approach

Search-as-you-type Location field (evaluated against: auto-open dropdown,
confirm dialog — all share the no-wrong-data gate; search was chosen for the
most standard-app feel and the fewest follow-up actions).

## Design

### 1. Search control (user-facing)

- The inspector's Location editor becomes a live search: TextBox + results
  list. Query on ≥2 characters, 300 ms debounce, stale responses discarded
  (version token).
- Every result is labeled **"City, State, Country · population"** (e.g.
  "Berlin, New Hampshire, United States · 9k"), carrying exact coordinates.
- Cities **and postal codes** both query the Open-Meteo geocoder (it resolves
  both as a `name` query) — typing "10115" also yields a verified pick.
- Picking a result is the only way the widget commits a location change.

### 2. Pick semantics (deterministic)

On pick the widget persists, through the existing `SetProperty` /
`PersistProperty` funnel (profile marked dirty):

- `Location` = the candidate's readable label ("Berlin, New Hampshire, United States")
- `Latitude` / `Longitude` = the candidate's exact coordinates
- `LocationMatch` cleared (the search control replaces the dropdown)

Coordinates are the truth; the label is display-only. No re-geocoding for a
picked place — restarts, imports, and the in-app updater all preserve the same
city.

### 3. Ambiguity gate (no wrong data)

When the user types a name and does **not** pick (blur/enter without a
selection), the typed text still resolves through the existing path:

- Unambiguous name (e.g. "Victoria") → fetches instantly, as today.
- Ambiguous name (multiple candidates tied at the top rank, winner decided by
  population — e.g. the four "Berlins") → **no fetch**; the widget enters a
  "Berlin — select which one" state (no weather rendered) and the search list
  re-opens.

ZIP codes and lat/lon typed directly keep today's behavior as the fallback.

### 4. Module changes

- **`WeatherClient.SearchCitiesAsync(string query, CancellationToken)`** —
  public; builds the search URL, parses results into the existing
  `GeocodeCandidate` records, returns an empty list on error (never throws);
  cancellation flows to the HTTP call. The URL + result parsing is extracted
  from `GeocodeCityLocationAsync` (single geocoder call site).
- **`IWidgetLocationSearch`** (Widgets) — new capability contract; the
  `WeatherForecastWidget` implements it by delegating to its `WeatherClient`
  (the `IWidgetPropertyOptionsProvider` precedent — the inspector discovers it
  via `widget.ActiveInstance` and stays widget-agnostic).
- **`InspectorPanelRenderer`** — when the widget implements the contract, the
  Location property renders the search editor (TextBox + results list) instead
  of the plain text editor; picks commit through the existing
  `ApplyInspectorPropertyValue` write-back seam. No new window state.
- **Widget resolution flow** — the gate extends the existing
  location-resolution outcome: a population-decided tie without a pick sets a
  "needs selection" flag (no fetch, render prompt).

### 5. Data flow

```
user types ≥2 chars
  → editor debounce (300 ms)
  → IWidgetLocationSearch.SearchAsync (version token)
  → WeatherClient.SearchCitiesAsync → Open-Meteo geocoder
  → results list (label · population) in the inspector

user picks a result
  → ApplyInspectorPropertyValue (Location label, Lat, Lon; LocationMatch cleared)
  → SetProperty/PersistProperty → profile dirty → property-change force fetch
  → FetchCurrentAsync with explicit coordinates (no geocode)

user types a name without picking
  → existing resolution; tie at top rank?
      → yes: no fetch, "select which one" state, list re-opens
      → no:  fetch as today
```

## Testing

- `WeatherClientTests` — `SearchCitiesAsync`: mapped candidates, postal-code
  query, error → empty list, cancellation honored.
- `WeatherForecastWidgetTests` — pick commits Location/Lat/Lon through
  `SetProperty` (PropertyValues persist); ambiguous bare name without pick →
  no fetch + select state; unambiguous name → fetches immediately.
- `InspectorPanelRendererTests` — Location renders the search editor when the
  contract is implemented; pick flows through the write-back seam.

## Out of scope (YAGNI)

- US-state abbreviation matching (moot with search).
- Auto-geolocation ("use my location").
- Search for other text properties.
- The Location Match dropdown (superseded by the search control).
