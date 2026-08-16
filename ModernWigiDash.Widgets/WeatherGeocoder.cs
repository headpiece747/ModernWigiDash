using System.Buffers;
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
/// The geocoding HTTP + parse adapter behind <see cref="WeatherClient"/>: the
/// Open-Meteo city search (inspector + resolution) and the zippopotam ZIP
/// lookup, shaped into resolver candidates and dropdown options. Pure policy
/// (ranking, ambiguity gate, alias tables) stays in
/// <see cref="WeatherLocationResolver"/>; resolved-state application stays in
/// the client. Never throws for HTTP failures — it logs and returns the
/// empty/fallback shape, so the widget's no-data state renders instead.
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
    public WeatherGeocoder(HttpClient http, Action<string, Exception?>? logError = null)
    {
        _http = http;
        _logError = logError;
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
    /// <summary>Test seam: overrides the per-leg deadline (see
    /// <see cref="ReadBoundedAsync"/>) so the timeout path is drivable
    /// without waiting the real 30 s.</summary>
    internal static TimeSpan? HttpTimeoutOverride { get; set; }

    /// <summary>
    /// The bounded HTTP text read behind every fetch leg, replacing
    /// <see cref="HttpClient.GetStringAsync(string, CancellationToken)"/>:
    /// headers are read first, then the body is streamed up to the declared
    /// content length (or the 2 MB cap when the server omits one). A response
    /// that DECLARES more than the cap is rejected before any body is read; a
    /// chunked response is truncated at the cap (the JSON parse then fails and
    /// the caller falls back like any failure). Non-success responses throw
    /// like <c>GetStringAsync</c>, so the callers' existing catch→log→null
    /// semantics are unchanged.
    /// </summary>
    internal static async Task<string> ReadBoundedAsync(HttpClient http, string url, CancellationToken cancellationToken)
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
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxResponseBytes)
            {
                throw new HttpRequestException($"HTTP response exceeds the {MaxResponseBytes} byte bound");
            }
            long cap = declared is > 0 ? declared.Value : MaxResponseBytes;
            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                while (total < cap)
                {
                    int remaining = (int)Math.Min(chunk.Length, cap - total);
                    int read = await stream.ReadAsync(chunk.AsMemory(0, remaining), timeoutCts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    await buffer.WriteAsync(chunk.AsMemory(0, read), timeoutCts.Token).ConfigureAwait(false);
                }
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Either the internal deadline OR the shared client's own Timeout
            // fired (the request CTS cancels independently of the linked one)
            // — the caller's token did not. Report the hang as a failure so
            // the callers log it and stamp the throttle.
            throw new TimeoutException($"HTTP leg exceeded the {HttpTimeout.TotalSeconds}s deadline");
        }
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
            string json = await ReadBoundedAsync(_http, WeatherLocationResolver.BuildSearchUri(query, null).ToString(), cancellationToken).ConfigureAwait(false);
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
            _logError?.Invoke($"Location search failed for '{SanitizeLog(query)}': {ex.Message}", ex);
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

            string json = await ReadBoundedAsync(_http, url, cancellationToken).ConfigureAwait(false);
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
                var dropdown = candidates.Select(c => ToDropdownCandidate(c, namePart)).ToArray();

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
            _logError?.Invoke($"Geocoding failed for '{SanitizeLog(query)}': {ex.Message}", ex);
            return new WeatherCityGeocodeResult.Unresolved([]);
        }
    }

    /// <summary>
    /// One ZIP lookup via zippopotam; null when the lookup failed (the caller
    /// falls back to the worldwide Open-Meteo geocoder with the original
    /// location so the CountryCode hint is carried).
    /// </summary>
    public async Task<WeatherZipGeocodeResult?> GeocodeZipAsync(string zipCode, string? countryCode, CancellationToken cancellationToken = default)
    {
        string trimmed = zipCode.Trim();
        try
        {
            string url = WeatherLocationResolver.BuildZipLookupUri(trimmed, countryCode).ToString();
            string json = await ReadBoundedAsync(_http, url, cancellationToken).ConfigureAwait(false);
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
            double lat = double.Parse(place.GetProperty("latitude").GetString()!, CultureInfo.InvariantCulture);
            double lon = double.Parse(place.GetProperty("longitude").GetString()!, CultureInfo.InvariantCulture);
            // The remote response is the cluster's third coordinate entry
            // point — NaN/out-of-range strings must not flow into the forecast
            // URL (reject the row like the city-leg validation).
            if (!IsValidCoordinate(lat, lon))
            {
                throw new InvalidOperationException("zippopotam response carried unusable coordinates");
            }
            string city = place.TryGetProperty("place name", out var name) ? name.GetString() ?? "" : "";
            string state = place.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            // Compose only the non-empty parts: a zippopotam response that
            // omits the place name must not produce a ", Texas" label.
            string cityTrim = city.Trim();
            string stateTrim = state.Trim();
            string label;
            if (cityTrim.Length == 0) label = stateTrim;
            else if (stateTrim.Length == 0) label = cityTrim;
            else label = $"{cityTrim}, {stateTrim}";
            return new WeatherZipGeocodeResult(lat, lon, label);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"ZIP geocoding failed for '{SanitizeLog(trimmed)}': {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Parses a "lat,lon" location query into its two components;
    /// false when the query is not a two-part coordinate pair OR the values
    /// are not usable coordinates (NaN/Infinity — "NaN" and "Infinity" parse
    /// as valid doubles — or out of the lat/lon ranges).
    /// </summary>
    public static bool TryParseCoordinatePair(string query, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        string[] parts = query.Split(',');
        if (parts.Length != 2) return false;
        return double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
            && IsValidCoordinate(lat, lon);
    }

    /// <summary>A coordinate pair is usable only when both values are finite
    /// and inside the lat/lon ranges (|lat| ≤ 90, |lon| ≤ 180). The range
    /// check exists because "NaN"/"Infinity" PARSE as valid doubles — a
    /// non-finite coordinate would silently poison every downstream URL.</summary>
    public static bool IsValidCoordinate(double lat, double lon)
        => !double.IsNaN(lat) && !double.IsNaN(lon)
            && !double.IsInfinity(lat) && !double.IsInfinity(lon)
            && Math.Abs(lat) <= 90
            && Math.Abs(lon) <= 180;

    /// <summary>The display compose for a resolved coordinate identity:
    /// "lat, lon" with two invariant decimals.</summary>
    public static string FormatCoordinates(double lat, double lon)
        => $"{lat.ToString("F2", CultureInfo.InvariantCulture)}, {lon.ToString("F2", CultureInfo.InvariantCulture)}";

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
            || !IsValidCoordinate(lat, lon))
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

    /// <summary>
    /// Flattens and BOUNDS user-provided strings before interpolation into
    /// log lines: embedded newlines cannot inject fake entries, and a
    /// multi-megabyte Location value cannot write a multi-megabyte line.
    /// </summary>
    private static string SanitizeLog(string value)
    {
        string flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 200 ? flat : flat[..200];
    }
}
