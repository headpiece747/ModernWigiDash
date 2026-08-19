using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The outcome of one city-name geocode. The candidates ride on every shape —
/// they populate the widget's "Location Match" dropdown even when the
/// resolution is ambiguous. The client applies resolved state only from
/// <see cref="Resolved"/>; the other shapes stamp the attempt time (throttle).
/// </summary>
internal abstract record WeatherCityGeocodeResult(IReadOnlyList<GeocodeCandidate> Candidates)
{
    /// <summary>The resolver picked a unique winner: its exact coordinates,
    /// composed label, and population.</summary>
    public sealed record Resolved(IReadOnlyList<GeocodeCandidate> Candidates, double Lat, double Lon, string Label, double Population)
        : WeatherCityGeocodeResult(Candidates);

    /// <summary>The candidates tie — coordinates must not be guessed.</summary>
    public sealed record Ambiguous(IReadOnlyList<GeocodeCandidate> Candidates)
        : WeatherCityGeocodeResult(Candidates);

    /// <summary>The geocode produced no candidates (fetch failure or an empty
    /// response) — the previous resolution stays valid.</summary>
    public sealed record Unresolved(IReadOnlyList<GeocodeCandidate> Candidates)
        : WeatherCityGeocodeResult(Candidates);
}

/// <summary>A resolved ZIP lookup: the place's coordinates and composed
/// "City, State" name; null when zippopotam failed (the caller falls back to
/// the worldwide geocoder).</summary>
internal sealed record WeatherZipGeocodeResult(double Lat, double Lon, string CityName);

/// <summary>
/// The outcome of the cluster's single resolution door (<see
/// cref="WeatherGeocoder.ResolveAsync"/>): one verdict for every input
/// spelling the ladder sees — explicit coordinates, a "lat,lon" pair, a
/// postal code, a "Location Match" pick, or a city name. A resolved outcome
/// carries the exact coordinates, the composed display label (the explicit
/// and pick legs honor the custom label; the coordinate-pair and ZIP legs
/// never do — the per-leg composition is the verbatim rule), and the
/// winner's population (0 for every non-name resolution);
/// <c>RefreshedCandidates</c> is non-null only when the city leg ran and
/// shaped the "Location Match" dropdown. Ambiguous carries the tie's
/// candidates (coordinates are never guessed); Unresolved means the geocode
/// produced nothing (the previous resolution stays valid). Applying the
/// state and stamping the throttle stay with the client.
/// </summary>
internal abstract record WeatherResolutionOutcome
{
    /// <summary>A unique winner — or a zero-HTTP fast path (explicit
    /// coordinates, a pair, a ZIP, a pick): its coordinates and label.</summary>
    public sealed record Resolved(double Lat, double Lon, string Label, double Population, IReadOnlyList<GeocodeCandidate>? RefreshedCandidates = null)
        : WeatherResolutionOutcome;

    /// <summary>The candidates tie — coordinates must not be guessed.</summary>
    public sealed record Ambiguous(IReadOnlyList<GeocodeCandidate> Candidates)
        : WeatherResolutionOutcome;

    /// <summary>The geocode produced no candidates — the previous resolution
    /// stays valid.</summary>
    public sealed record Unresolved : WeatherResolutionOutcome;
}

/// <summary>
/// The geocoding HTTP + parse adapter behind <see cref="WeatherClient"/>: the
/// Open-Meteo city search (inspector + resolution) and the zippopotam ZIP
/// lookup, shaped into resolver candidates and dropdown options. Pure policy
/// AND decision rules (ranking, ambiguity gate, alias tables, coordinate
/// validity, place-label composition) stay in
/// <see cref="WeatherLocationResolver"/> — the adapter is transport + JSON
/// shape only; resolved-state application stays in the client. Never throws
/// for HTTP failures — it logs and returns the empty/fallback shape, so the
/// widget's no-data state renders instead.
/// Cancellation propagates (the teardown contract), like every other fetch leg.
/// </summary>
internal sealed class WeatherGeocoder
{
    private HttpClient _http;
    private readonly Action<string, Exception?>? _logError;

    /// <param name="http">The transport for geocoding requests. The owning
    /// <see cref="WeatherClient"/> passes its live client here and syncs its
    /// test seam via <see cref="Http"/>, so every leg of the cluster rides the
    /// same transport (and the same 30s timeout / bounded read).</param>
    /// <param name="logError">Optional error sink; when omitted, failures are silent.</param>
    /// <param name="httpTimeoutOverride">Test seam: a per-instance override of
    /// the per-leg body-read deadline (defaults to <see cref="HttpTimeout"/>).</param>
    public WeatherGeocoder(HttpClient http, Action<string, Exception?>? logError = null, TimeSpan? httpTimeoutOverride = null)
    {
        _http = http;
        _logError = logError;
        HttpTimeoutOverride = httpTimeoutOverride;
    }

    /// <summary>The live transport. The client syncs this whenever its own
    /// <c>TestHttpClient</c> changes, so the geocoder always fetches through
    /// the same seam the forecast fetch uses.</summary>
    internal HttpClient Http
    {
        get => _http;
        set => _http = value;
    }

    /// <summary>The bounded-read cap for every HTTP leg (geocoding + forecast):
    /// a response larger than this is rejected before it is buffered.</summary>
    internal const long MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>The per-leg body-read deadline (the shared client's
    /// <c>Timeout</c> only bounds the header phase under ResponseHeadersRead).</summary>
    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Per-instance test seam: overrides the per-leg deadline (see
    /// <see cref="ReadBoundedAsync"/>) so the timeout path is drivable without
    /// waiting the real 30 s. Injected at construction (never a process-wide
    /// knob — one test's override can no longer leak into every other
    /// geocoder's deadline).</summary>
    internal TimeSpan? HttpTimeoutOverride { get; set; }

    /// <summary>
    /// The bounded HTTP text read behind every fetch leg, replacing
    /// <see cref="HttpClient.GetStringAsync(string, CancellationToken)"/>:
    /// headers are read first, then the body is streamed through the shared
    /// <see cref="BoundedRead"/> core up to the declared content length (or
    /// the 2 MB cap when the server omits one). A response that DECLARES more
    /// than the cap is rejected before any body is read; a chunked response
    /// is truncated at the cap (the JSON parse then fails and the caller
    /// falls back like any failure). Non-success responses throw like
    /// <c>GetStringAsync</c>, so the callers' existing catch→log→null
    /// semantics are unchanged.
    /// </summary>
    internal async Task<string> ReadBoundedAsync(string url, CancellationToken cancellationToken)
    {
        // HttpClient.Timeout only bounds the header phase under
        // ResponseHeadersRead - the streamed body read below needs its own
        // deadline, or a slow-drip server could hold the leg indefinitely.
        // The internal deadline must NOT surface as a plain cancellation: an
        // OCE is indistinguishable from caller teardown, and the callers'
        // failure path (log + throttle stamp) would be skipped, leaving a
        // silent 30s retry loop during an outage. Convert it to a failure.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HttpTimeoutOverride ?? HttpTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxResponseBytes)
            {
                throw new HttpRequestException($"HTTP response exceeds the {MaxResponseBytes} byte bound");
            }
            long cap = declared is > 0 ? declared.Value : MaxResponseBytes;
            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] body = await BoundedRead.ReadAsync(stream, cap, timeoutCts.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Either the internal deadline OR the shared client's own Timeout
            // fired (the request CTS cancels independently of the linked one)
            // — the caller's token did not. Report the hang as a failure so
            // the callers log it and stamp the throttle. The message states
            // the EFFECTIVE deadline (the override, when the test seam set
            // one), not the default.
            double effectiveSeconds = (HttpTimeoutOverride ?? HttpTimeout).TotalSeconds;
            throw new TimeoutException($"HTTP leg exceeded the {effectiveSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s deadline");
        }
    }

    /// <summary>
    /// The forecast leg: builds the forecast query URL (the resolver's
    /// invariant F4 formatting — a comma-decimal OS locale must never
    /// interpolate "40,7100" into the query at a call site) and performs the
    /// bounded read. Every weather fetch in the cluster rides this door; HTTP
    /// failures propagate to the caller's catch (the client's Failed/Stale
    /// verdict), like the raw leg.
    /// </summary>
    public Task<string> ReadForecastAsync(double lat, double lon, CancellationToken cancellationToken)
        => ReadBoundedAsync(WeatherLocationResolver.BuildForecastUri(lat, lon).ToString(), cancellationToken);

    /// <summary>
    /// The cluster's single resolution door: walks the ladder — explicit
    /// coordinates (authoritative, honoring the custom label), a "lat,lon"
    /// pair (never honoring it), a postal code (the zippopotam route the hint
    /// selects and, when that route 404s, the geocoder's own postal search —
    /// WITH the hint first, then without), a "Location Match" pick
    /// (honored ONLY on the non-postal path — a postal input never sees the
    /// pick — honoring the custom label), and finally the city-name geocode —
    /// and returns the verdict. Applies no resolved state (the client owns
    /// lat/lon/name application and the throttle stamp) and never throws for
    /// HTTP failures — the unresolved verdict is the failure shape.
    /// </summary>
    public async Task<WeatherResolutionOutcome> ResolveAsync(
        WeatherLocation location,
        IReadOnlyList<GeocodeCandidate>? candidates,
        CancellationToken cancellationToken)
    {
        // Explicit coordinates are authoritative — they must win over a stale
        // Location Match pick from a previous city query. The pair is only
        // honored when BOTH values are usable coordinates: "NaN"/"Infinity"
        // parse as doubles, so the range check is what rejects them (and the
        // resolution falls back to the location query instead of poisoning
        // the forecast URL).
        if (double.TryParse(location.Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out double explicitLat)
            && double.TryParse(location.Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out double explicitLon)
            && WeatherLocationResolver.IsValidCoordinate(explicitLat, explicitLon))
        {
            return new WeatherResolutionOutcome.Resolved(explicitLat, explicitLon,
                string.IsNullOrWhiteSpace(location.CustomLabel)
                    ? WeatherLocationResolver.FormatCoordinates(explicitLat, explicitLon)
                    : location.CustomLabel,
                0);
        }

        if (WeatherLocationResolver.TryParseCoordinatePair(location.Location, out double pairLat, out double pairLon))
        {
            return new WeatherResolutionOutcome.Resolved(pairLat, pairLon, WeatherLocationResolver.FormatCoordinates(pairLat, pairLon), 0);
        }

        if (WeatherLocationResolver.TryPostalRoute(location.Location, location.CountryCode, out string postalLookup, out string postalRoute))
        {
            // The postal leg routes by the resolver's rule: zippopotam is NOT
            // US-only (60+ countries — /de/10115 is Berlin, /fr/75001 is
            // Paris), the hint selects the route, and a numeric code without
            // a hint reads as US (the geocoder's postal index is US-biased
            // too — the least-wrong default; the resolved label stays
            // visible). The lookup key is the indexed shape (ZIP+4 -> 5
            // digits on the US route, GB/CA full code -> 3-char short form).
            WeatherZipGeocodeResult? postal = await GeocodeZipAsync(postalLookup, postalRoute, cancellationToken).ConfigureAwait(false);
            if (postal is not null)
            {
                return new WeatherResolutionOutcome.Resolved(postal.Lat, postal.Lon, postal.CityName, 0);
            }

            // The route 404ed (unsupported country or code) — the worldwide
            // geocoder's postal search is the fallback: its name parameter
            // accepts postal codes (a live 5-digit code resolves at least the
            // US ZIPs and some foreign codes).
            return await ResolvePostalFallbackAsync(location, cancellationToken).ConfigureAwait(false);
        }

        // A user pick from the "Location Match" dropdown resolves directly to
        // that candidate's exact coordinates — no re-geocode. The pick is only
        // honored on this non-ZIP path (after the override and ZIP paths); the
        // caller passes the CURRENT dropdown (cleared by the client's
        // InvalidateLocation on any location/coords change), so a stale pick
        // cannot win.
        if (candidates is { Count: > 0 } && !string.IsNullOrWhiteSpace(location.LocationMatch))
        {
            var match = candidates.FirstOrDefault(c =>
                c.Query.Equals(location.LocationMatch.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new WeatherResolutionOutcome.Resolved(match.Lat, match.Lon,
                    string.IsNullOrWhiteSpace(location.CustomLabel) ? match.Label : location.CustomLabel,
                    match.Population);
            }
        }

        return await ResolveCityLegAsync(location, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The city-name leg of the resolution door: the geocode plus the
    /// dropdown shaping, mapped onto the door's outcome union — a resolved
    /// winner carries its candidates so the client can refresh the dropdown,
    /// a tie carries the tie's candidates, and an empty geocode resolves
    /// nothing (the previous resolution stays valid).</summary>
    private async Task<WeatherResolutionOutcome> ResolveCityLegAsync(WeatherLocation location, CancellationToken cancellationToken)
    {
        WeatherCityGeocodeResult result = await GeocodeCityAsync(location.Location, location.CountryCode, location.LocationMatch, cancellationToken).ConfigureAwait(false);
        return MapCityGeocodeToOutcome(result);
    }

    /// <summary>
    /// The postal leg's fallback when the zippopotam route 404s: the
    /// geocoder's own postal search (its name parameter accepts postal
    /// codes — a live-probed 5-digit code resolves at least the US ZIPs and
    /// some foreign codes, while UK/CA alphanumeric codes resolve nothing).
    /// A hinted code searches WITH the hint first — the user's
    /// disambiguation is worth one (possibly empty) leg — and only a hinted
    /// leg that returned NOTHING retries without the hint (the geocoder's
    /// postal index is US-biased, and a hint the index lacks must not leave
    /// the user with no weather the geocoder could have resolved; a leg that
    /// DID answer is the complete verdict). The city pipeline ranks the
    /// answers under the no-guess rule: a single candidate resolves (the
    /// postal search returns the places whose postcode index carries the
    /// code), a cross-country tie returns the pick list.
    /// </summary>
    private async Task<WeatherResolutionOutcome> ResolvePostalFallbackAsync(WeatherLocation location, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(location.CountryCode))
        {
            WeatherCityGeocodeResult hinted = await GeocodeCityAsync(location.Location, location.CountryCode, null, cancellationToken).ConfigureAwait(false);
            if (hinted is not WeatherCityGeocodeResult.Unresolved)
            {
                return MapCityGeocodeToOutcome(hinted);
            }
        }

        return MapCityGeocodeToOutcome(await GeocodeCityAsync(location.Location, null, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The one mapping of the city-geocode outcome onto the door's
    /// outcome union — the city leg and the postal fallback both ride it, so
    /// the Resolved/Ambiguous/Unresolved shape cannot drift between them.
    /// The pick is passed through the city leg only: a postal input never
    /// sees a persisted Location Match (a pick from a previous city query
    /// must not answer a postal one).</summary>
    private static WeatherResolutionOutcome MapCityGeocodeToOutcome(WeatherCityGeocodeResult result)
        => result switch
        {
            WeatherCityGeocodeResult.Resolved r => new WeatherResolutionOutcome.Resolved(r.Lat, r.Lon, r.Label, r.Population, r.Candidates),
            WeatherCityGeocodeResult.Ambiguous a => new WeatherResolutionOutcome.Ambiguous(a.Candidates),
            _ => new WeatherResolutionOutcome.Unresolved(),
        };

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
            string json = await ReadBoundedAsync(WeatherLocationResolver.BuildSearchUri(query, null).ToString(), cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
            {
                return [];
            }

            var candidates = new List<GeocodeCandidate>(results.GetArrayLength());
            foreach (var candidate in results.EnumerateArray())
            {
                if (ParseGeocodeCandidate(candidate) is { } parsed)
                    candidates.Add(ToDropdownCandidate(parsed, query));
            }
            return candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Location search failed for '{LogSanitizer.Sanitize(query)}': {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>
    /// One city-name geocode: fetch, candidate parsing, dropdown shaping, and
    /// the resolver's decision — without applying any resolved state (the
    /// client owns the lat/lon/city application). The full <paramref name="query"/>
    /// is the log identity; the search uses its trimmed name part.
    /// </summary>
    public async Task<WeatherCityGeocodeResult> GeocodeCityAsync(string query, string? countryCode, string? locationMatch, CancellationToken cancellationToken = default)
    {
        string trimmed = query.Trim();
        try
        {
            var (namePart, suffixPart) = WeatherLocationResolver.SplitQuery(trimmed);

            string url = WeatherLocationResolver.BuildSearchUri(namePart, countryCode).ToString();
            string json = await ReadBoundedAsync(url, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                // Every candidate becomes a pickable option ("Location Match"
                // dropdown): label = "Name, Admin1, Country", query = the label
                // text so a pick re-resolves deterministically to this place.
                var candidates = new List<WeatherLocationResolver.Candidate>(results.GetArrayLength());
                foreach (var candidate in results.EnumerateArray())
                {
                    if (ParseGeocodeCandidate(candidate) is { } parsed)
                        candidates.Add(parsed);
                }
                if (candidates.Count == 0) return new WeatherCityGeocodeResult.Unresolved([]);
                // The RANKING sees every candidate (the fuzzy rows simply
                // score zero and can never win); the PICK LIST offers only
                // the exact-name candidates — fuzzy search rows ("Palmyra"
                // inside a live "Springfield" search) must never be
                // persistable as the user's location. A postal-shaped query
                // has no exact-name candidates by definition (the code
                // matched the places' postcode index, not their names), so
                // its pick list keeps every returned candidate — the
                // cross-country postal tie is only escapable through them.
                bool postalQuery = WeatherLocationResolver.TryPostalRoute(trimmed, countryCode, out _, out _);
                var dropdown = candidates
                    .Where(c => postalQuery || WeatherLocationResolver.IsExactNameMatch(c.Name, namePart))
                    .Select(c => ToDropdownCandidate(c, namePart))
                    .ToArray();

                return WeatherLocationResolver.Resolve(candidates, namePart, suffixPart, countryCode, locationMatch) switch
                {
                    WeatherLocationResolver.ResolveResult.Resolved r => new WeatherCityGeocodeResult.Resolved(dropdown, r.Lat, r.Lon, r.Label, r.Population),
                    _ => new WeatherCityGeocodeResult.Ambiguous(dropdown),
                };
            }
            return new WeatherCityGeocodeResult.Unresolved([]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Geocoding failed for '{LogSanitizer.Sanitize(query)}': {ex.Message}", ex);
            return new WeatherCityGeocodeResult.Unresolved([]);
        }
    }

    /// <summary>
    /// One postal-code lookup via zippopotam (60+ route countries — not
    /// US-only); null when the route 404s or the response is unusable (the
    /// caller then runs the postal fallback: the geocoder's own postal
    /// search, hinted first, then bare).
    /// </summary>
    public async Task<WeatherZipGeocodeResult?> GeocodeZipAsync(string zipCode, string? countryCode, CancellationToken cancellationToken = default)
    {
        string trimmed = zipCode.Trim();
        try
        {
            string url = WeatherLocationResolver.BuildZipLookupUri(trimmed, countryCode).ToString();
            string json = await ReadBoundedAsync(url, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // The real zippopotam shape nests the place under "places[0]"
            // with string coordinates — the earlier root-level numeric parse
            // matched a hand-made fixture, not the API, so every real ZIP
            // threw, logged, and silently fell back to the postal-code
            // geocoder (which resolves some US ZIPs to area centroids).
            if (!root.TryGetProperty("places", out var places) || places.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("zippopotam response has no places");
            }
            JsonElement place = places[0];
            // The tolerant getters apply here too: a malformed response with
            // a missing/null/non-string latitude must read as unusable
            // coordinates, not throw a raw exception into the caller's catch.
            string latStr = GetString(place, "latitude");
            string lonStr = GetString(place, "longitude");
            if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                throw new InvalidOperationException("zippopotam response carried unusable coordinates");
            }
            // The remote response is the cluster's third coordinate entry
            // point — NaN/out-of-range strings must not flow into the forecast
            // URL (reject the row like the city-leg validation).
            if (!WeatherLocationResolver.IsValidCoordinate(lat, lon))
            {
                throw new InvalidOperationException("zippopotam response carried unusable coordinates");
            }
            string city = GetString(place, "place name");
            string state = GetString(place, "state");
            // The label composition is the resolver's rule (the ZIP sibling
            // of ComposeLabel): only the non-empty parts are composed, so a
            // response that omits the place name cannot produce a ", Texas".
            string label = WeatherLocationResolver.ComposeZipLabel(city, state);
            return new WeatherZipGeocodeResult(lat, lon, label);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"ZIP geocoding failed for '{LogSanitizer.Sanitize(trimmed)}': {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>Parses one geocoder result into the resolver's raw candidate
    /// model. A candidate that lacks lat/lon (or carries a non-numeric value)
    /// returns null — the response loops skip that row instead of aborting
    /// the whole parse, so one malformed entry degrades only itself; name is
    /// null when the geocoder omitted it so the composed label can fall back
    /// to the query.</summary>
    private static WeatherLocationResolver.Candidate? ParseGeocodeCandidate(JsonElement candidate)
    {
        if (!TryReadDouble(candidate, "latitude", out double lat)
            || !TryReadDouble(candidate, "longitude", out double lon)
            // The same boundary rule as the explicit-coords and ZIP legs: a
            // candidate with non-finite/out-of-range coordinates must not
            // flow into the forecast URL — degrade the row, not the parse.
            || !WeatherLocationResolver.IsValidCoordinate(lat, lon))
        {
            return null;
        }
        return new WeatherLocationResolver.Candidate(
            candidate.TryGetProperty("name", out var n) ? n.GetString() : null,
            GetString(candidate, "admin1"),
            GetString(candidate, "country"),
            GetString(candidate, "country_code"),
            lat,
            lon,
            ReadPopulation(candidate));
    }

    private static bool TryReadDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value);
    }

    /// <summary>One resolver candidate → a pickable dropdown option: the
    /// resolved label doubles as the query so a pick re-resolves
    /// deterministically, and the population rides along for the search
    /// list's disambiguating suffix — the single spelling shared by the
    /// search and resolution paths.</summary>
    private static GeocodeCandidate ToDropdownCandidate(WeatherLocationResolver.Candidate candidate, string fallbackName)
    {
        string label = WeatherLocationResolver.ComposeLabel(candidate, fallbackName);
        return new GeocodeCandidate(label, label, candidate.Lat, candidate.Lon)
        {
            Population = candidate.Population,
        };
    }

    /// <summary>Reads a string field tolerantly: missing, null, or
    /// NON-STRING values all read as "" — one malformed field must not abort
    /// the whole response parse (the lat/lon reads are guarded the same way).</summary>
    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>The candidate's reported population (0 when the geocoder
    /// omitted it or the value is unrepresentable) — the search list's
    /// disambiguating label data and the resolution winner's exposed
    /// population. TryGetDouble, not GetDouble: a JSON literal like 1e400 is
    /// valid JSON but unrepresentable, and must degrade the row, not abort
    /// the whole response parse.</summary>
    private static double ReadPopulation(JsonElement candidate)
        => candidate.TryGetProperty("population", out var p)
           && p.ValueKind == JsonValueKind.Number
           && p.TryGetDouble(out double population)
            ? population
            : 0;
}
