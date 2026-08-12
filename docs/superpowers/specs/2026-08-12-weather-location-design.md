# Weather Location Accuracy — Design

- **Date:** 2026-08-12
- **Status:** Approved (implementing)

## Problem

`WeatherClient.GeocodeCityLocationAsync` queries Open-Meteo geocoding with
`count=1` and blindly takes `results[0]`. Open-Meteo ranks by population and
importance, so ambiguous city names resolve to the wrong place worldwide:
"Victoria" -> Vitoria, Brazil; "Springfield" -> Missouri; "San Jose" ->
California. There is no disambiguation path, ZIP geocoding is US-only
(zippopotam `/us/`), and the widget title shows only the bare city name, so a
wrong resolution is invisible to the user.

## Fix (three parts, worldwide)

### 1. Smart geocoding ranking — `WeatherClient.GeocodeCityLocationAsync`

- Query `count=10` instead of `count=1`.
- Rank candidates:
  1. Exact case-insensitive name match (the first token of the query).
  2. Comma-suffix match: "Springfield, MA" / "Victoria, BC" / "San Jose,
     Costa Rica" — the suffix (after the first comma) matches the result's
     `admin1` (state/province), `country`, or `country_code` (any of the
     three, case-insensitive).
  3. Population tiebreak (the API returns `population`).
- The resolved name becomes `"Name, Admin1, Country"` (omitting admin1 when
  absent) so the widget title shows exactly what was picked.

### 2. Optional "Country Code" widget property

- New `[WidgetProperty("Country Code", Text)]` on `WeatherForecastWidget`
  (e.g. "US", "DE", "CA", "JP").
- `WeatherLocation` gains a `CountryCode` field; the widget's `BuildLocation`
  passes it.
- When set, appended to the geocode URL as `countryCode=US` — the
  worldwide disambiguator for same-named cities across countries (verified:
  `San Jose&countryCode=CR` -> Costa Rica).

### 3. ZIP codes worldwide

- The zippopotam US fast-path stays (its nice "City, State" label), and its
  fallback already re-runs the now-smarter Open-Meteo geocoder, so numeric
  postal codes resolve worldwide (e.g. "10115" + "DE").
- Non-numeric postal codes (e.g. UK "SW1A 1AA") are not auto-detected as ZIPs
  — out of scope (the user can use city + country code).

## Untouched

- lat/lon overrides (already accurate), cache/throttle, rendering, fetch
  pipeline, and all other `WeatherLocation` callers' behavior (the new field
  is optional/nullable).

## Testing

Extend `WeatherClientTests` via the `StubHttpHandler` seam:
- Ambiguous name + state suffix picks the right admin1 over a wrong same-named
  candidate.
- Country-code hint is present in the geocode URL and filters the pick.
- Exact-name match beats higher-population fuzzy match.
- Resolved-name composition ("Name, Admin1, Country").
- Existing tests stay green (the new `WeatherLocation` field is optional).
