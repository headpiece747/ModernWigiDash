# Weather Location UX Revision — Design

**Status:** Approved (2026-08-14)
**Supersedes:** parts of `2026-08-14-weather-location-search-design.md` (shipped, then revised after on-device feedback)
**Scope:** the location field's display + pick semantics + ambiguity handling

## Problem (on-device feedback)

The shipped search-as-you-type feature (un-pushed on master) prompted the user
to reject two behaviors:

1. Typing an ambiguous bare name without picking showed a **"Berlin — select
   which one"** state. The user wants no prompt state — the default experience
   is the configured location's weather (e.g. New York).
2. Picking auto-filled **Latitude/Longitude**. The user wants the **name** to
   be the location: the Location field shows the resolved place (with
   population), and Lat/Lon boxes stay empty unless manually entered.

## Behavior

- **The Location field shows the resolved place.** On a successful geocode
  resolution, the widget writes the resolved label into `Location` (typing
  "New York" ends with the field reading "New York, New York, United States").
  The editor appends the population for display ("… · 8.4M") from the last
  resolution — population is display-only, never persisted. The persisted
  property is always the plain label, so restarts, imports, and the in-app
  updater round-trip deterministically.
- **Picking a search result sets only `Location`** to the picked label
  ("Berlin, New Hampshire, United States"). Lat/Lon boxes stay empty unless
  the user types into them — the name is the truth.
- **Ambiguous typed names are silent.** No fetch, no prompt, no state change —
  the widget keeps showing the last good weather until a pick resolves the
  ambiguity. The "select which one" render state is deleted.

## Mechanics

### Multi-component suffix resolution (determinism enabler)

`GeocodeCityLocationAsync` splits a comma suffix into components and requires
**each** to match a candidate: "New Hampshire, United States" → admin1 "New
Hampshire" *and* country "United States" must both match. "Berlin, New
Hampshire, United States" → name "Berlin" + both components → Berlin NH wins
uniquely. The existing per-component equals/starts-with rule applies per
component (so "Springfield, MA" behavior is unchanged).

### Resolved-label write-back without a fetch loop

After a successful geocode, the widget writes the resolved label to `Location`
**only when it differs**, under a suppression flag so the write's
`OnPropertyChanged` does not re-fire a fetch — stable after one extra
resolution at most.

### Widget cleanup

- `_needsLocationSelection` + the prompt render are removed.
- `CommitPick` shrinks to one `SetProperty(Location, label)`.
- The client's ambiguity flag stays — it silently suppresses the fetch.

### Editor

Box seeds from `Location` + population (the `IWidgetLocationSearch` contract
gains a `double? CurrentPopulation` member — the last resolution's winner
population, refreshed on fetch; 0/null renders no suffix); the search list,
Enter/LostFocus commit, and version-token staleness stay as built.

## Testing

- Multi-component resolution: "New York, New York, United States" → NY;
  "Berlin, New Hampshire, United States" → Berlin NH; a non-matching
  component → no match; single-component suffixes unchanged.
- Pick sets `Location` only (Lat/Lon untouched; PropertyValues persist the
  label).
- Resolved-label write-back converges without a fetch loop.
- Ambiguous typed name → no fetch, no prompt, snapshot unchanged.
- Editor displays label + population.
