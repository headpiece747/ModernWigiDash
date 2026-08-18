# ADR-0006: One owner of the weather stale-identity invariant

**Date:** 2026-08-18
**Status:** Accepted
**Deciders:** Project owner

## Context

The weather cluster's load-bearing rule — "never apply weather resolved for a
different place than the UI shows" — was spelled in four-plus ways and held
together by comments alone, with no test surface (the 2026-08-18 architecture
review, `docs/reports/architecture-review-20260818-114407.html`, Card 1):

1. **The client's capture window** (`WeatherClient.FetchCurrentAsync`): the
   fetch-start query key + the post-save re-validation that converts the
   verdict to `Stale` when an invalidation lands in the save window.
2. **The cache-load identity stamp** (`WeatherClient.LoadCacheAsync`): a cache
   saved under a different query key must not be applied.
3. **The widget's return→apply re-check** (`WeatherForecastWidget`): the
   `StillCurrent` comparison at every await boundary plus the
   `locationKeyBefore` guard in the boot-time cache load.
4. **The key construction itself** (`WeatherClient.BuildQueryKey` +
   `EscapeKeyField`): a private spelling of "which place is this identity"
   that the widget also called through the client.

The `Fetched`/`Stale` outcome records already carry the `QueryKey` — the rule
had its data but no single predicate or owner.

## Decision

**The resolution identity has one owner: `ModernWigiDash.Widgets.WeatherQueryKey`.**

- `Build(WeatherLocation)` is the ONLY query-key construction: six identity
  fields (`LocationType|Location|Latitude|Longitude|CountryCode|LocationMatch`),
  backslash-escaped, `|`-joined — a separator inside a field can never forge a
  colliding key. `CustomLabel` is deliberately absent: a label edit must not
  re-fetch.
- `SameKey(left, right)` is the ONLY identity predicate: ordinal, null-safe
  (case is identity — a case change is a new place).
- `KeyPropertyNames` (6), `InvalidationProperties` (5 — every key field except
  `LocationMatch`), and `LocationMatchProperty` declare the identity field
  sets exactly once. `WeatherResolvedIdentity.ResolutionInvalidationProperties`
  is an alias of `InvalidationProperties`, so the widget's derived drift test
  pins the owner's set to the `WeatherLocation` record.
- Every guard site routes through the owner: the client's fetch-start capture,
  the cache-load stamp compare, `TryApplyCacheIdentity`, and the widget's
  `fetchKey` / `StillCurrent` / `locationKeyBefore`. `WeatherClient` no longer
  builds geocoding-identity strings.
- The test surface is the module itself: `WeatherQueryKeyTests` pins the key
  format, the escaping rule, the field-coverage rule derived from the record,
  the re-fetch-set + LocationMatch coverage, and the ordinal predicate — no
  pixels, no fetch mocks.

## Consequences

**Positive:**
- One module carries the whole stale-identity rule; the guard is assertable in
  one place (a silent drift in any spelling is now a failing test).
- A new resolution input fails two derived tests (record-derived field
  coverage in `WeatherQueryKeyTests` + the widget's drift pin) until it is
  wired into the key AND the invalidation set.
- The client's surface shrinks (no key builders) — the surface the geocoder
  single-door work (review Card 2) then targets.

**Negative:**
- One more module in the cluster (deliberately small: static, no state, no
  I/O).
- The identity is a string (not a struct): forced on the cache stamp, which is
  persisted as a string in the payload.

## Alternatives considered

1. **Better comments at the existing guard sites** — comments already failed
   to keep the spellings in sync (the review found four-plus), and comments
   provide no test surface.
2. **Move the key into `WeatherFetchControl`** — the key is also the cache
   stamp's identity and the widget's guard input; the fetch control would
   gain a cross-module concern. The review keeps fetch control deep: it owns
   the stamp machinery, not the identity rule.
3. **A struct identity record instead of an escaped string** — the stamp is
   persisted in the cache file as a string, so a struct would need its own
   stamp serialization; the escaped string is uniform across all three guard
   sites and forgery-proof.

## Date

2026-08-18