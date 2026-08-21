using System.Text;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure geocoding decision module for the weather location resolution:
/// candidate scoring (exact name, comma-suffix, country-code hint), the
/// country alias table, diacritic-insensitive matching, the ambiguity gate,
/// the same-country population tiebreak, the Location Match pick promotion,
/// the query/ZIP routing helpers, the coordinate validity rules, and the
/// place-label compositions (city, ZIP, coordinate) — the ONE home of the
/// "compose a display name from place parts" rule family. <see
/// cref="WeatherClient"/> keeps the HTTP fetch, the JSON parsing, and the
/// resolved-state application; this module owns the rules so the ranking is
/// assertable without a client instance. The module is a LEAF utility: both
/// <see cref="WeatherGeocoder"/> (decisions + URL builders) and <see
/// cref="WeatherClient"/> (the forecast URL and the ZIP routing pre-check)
/// call it directly — routing either through the geocoder would be a facade
/// with no policy behind it.
/// </summary>
internal static class WeatherLocationResolver
{
    /// <summary>
    /// One raw geocoder result, as the ranking rules need it. <see cref="Name"/>
    /// is null when the geocoder omitted it (the composed label falls back to
    /// the query); the other strings are "" when missing — the same semantics
    /// the original JSON accessors produced.
    /// </summary>
    public sealed record Candidate(string? Name, string Admin1, string Country, string CountryCode, double Lat, double Lon, double Population);

    /// <summary>The resolution outcome for one candidate set.</summary>
    public abstract record ResolveResult
    {
        /// <summary>
        /// A single unambiguous winner (or a promoted Location Match pick):
        /// its exact coordinates, composed label, and population.
        /// </summary>
        public sealed record Resolved(double Lat, double Lon, string Label, double Population) : ResolveResult;

        /// <summary>
        /// A tie the rules refuse to break — coordinates must stay unresolved
        /// until the user picks from the candidates.
        /// </summary>
        public sealed record Ambiguous : ResolveResult;

        /// <summary>The geocoder returned no candidates.</summary>
        public sealed record NoMatch : ResolveResult;
    }

    /// <summary>
    /// Splits a trimmed location query into its name part and comma-suffix
    /// part ("Springfield, MA" → name "Springfield", suffix "MA"; an empty
    /// suffix reads as null). A leading comma does not split — the original
    /// query shape is preserved for the geocoder.
    /// </summary>
    public static (string NamePart, string? SuffixPart) SplitQuery(string query)
    {
        string trimmed = query.Trim();
        string namePart = trimmed;
        string? suffixPart = null;
        int comma = trimmed.IndexOf(',');
        if (comma > 0)
        {
            namePart = trimmed[..comma].Trim();
            suffixPart = trimmed[(comma + 1)..].Trim();
            if (suffixPart.Length == 0) suffixPart = null;
        }
        return (namePart, suffixPart);
    }

    /// <summary>
    /// The postal-code routing decision: whether the location query is a
    /// postal code, the code shape the zippopotam lookup sends, and the
    /// route country. Numeric codes (a 2–10 digit code, or a hyphenated
    /// n-n code — US ZIP+4, JP "100-0001", PT "0000-000", BR "01000-000")
    /// always enter the postal leg: with a country hint the hint routes the
    /// lookup, and WITHOUT a hint the route is "us" — the geocoder's own
    /// postal index is US-biased ("10115" reads as the Manhattan ZIP there
    /// too), so the US route is the least-wrong default; the resolved city
    /// label stays visible in the widget title, never silent. Alphanumeric
    /// codes (GB "M1 1AA", CA "K1A 0A1" — a letter+digit mix; a bare city
    /// name like "Paris" never matches) carry no self-describing route, so
    /// they enter the postal leg ONLY under an explicit country hint; a
    /// hintless alphanumeric query keeps the city-name leg (it resolves
    /// nothing there today anyway — the failure is safe either way).
    /// The lookup key is the indexed shape, not the typed string
    /// (<see cref="NormalizePostalLookup"/>).
    /// </summary>
    public static bool TryPostalRoute(string query, string? countryCode, out string postalLookup, out string routeCountry)
    {
        postalLookup = "";
        routeCountry = "";
        string trimmed = query.Trim();
        if (trimmed.Length == 0) return false;

        bool numeric = IsNumericPostalCode(trimmed);
        if (!numeric && !IsAlphanumericPostalCode(trimmed)) return false;
        if (!numeric && string.IsNullOrWhiteSpace(countryCode)) return false;

        // The route: the hint when present, the US default otherwise (a numeric
        // code without a hint reads as US — the geocoder's own postal index
        // is US-biased too, so the least-wrong default; an alphanumeric code
        // cannot reach here without a hint).
        routeCountry = string.IsNullOrWhiteSpace(countryCode)
            ? UsRoute
            : countryCode.Trim().ToLowerInvariant();
        postalLookup = NormalizePostalLookup(trimmed, routeCountry);
        return true;
    }

    /// <summary>The numeric postal-code shapes: a bare 2–10 ASCII-digit
    /// code (US ZIP, DE/FR/ES/IT 5-digit, NO/SE 4-digit, IN 6-digit) or a
    /// hyphenated n-n code with a 3–5 digit group and a 3–4 digit group
    /// (US ZIP+4 "12345-6789", JP "100-0001", PT "0000-000", BR "01000-000").
    /// ASCII digits only — Unicode digit scripts must not route into the
    /// postal leg.</summary>
    private static bool IsNumericPostalCode(string trimmed)
    {
        string[] groups = trimmed.Split('-');
        if (groups.Length == 1)
            return groups[0].Length is >= 2 and <= 10 && groups[0].All(char.IsAsciiDigit);
        if (groups.Length == 2)
            return groups[0].Length is >= 3 and <= 5 && groups[0].All(char.IsAsciiDigit)
                && groups[1].Length is >= 3 and <= 4 && groups[1].All(char.IsAsciiDigit);
        return false;
    }

    /// <summary>An alphanumeric postal code: letter AND digit mix, ASCII
    /// only, at most one internal space, 4–9 compact characters (GB "M1 1AA",
    /// CA "K1A 0A1"). A bare name ("Paris", "Springfield") has no digit and
    /// never matches; a bare number has no letter.</summary>
    private static bool IsAlphanumericPostalCode(string trimmed)
    {
        if (trimmed.Length is < 4 or > 10) return false;
        int spaces = 0;
        bool hasLetter = false, hasDigit = false;
        foreach (char c in trimmed)
        {
            if (c == ' ')
            {
                spaces++;
                if (spaces > 1) return false;
            }
            else if (char.IsAsciiLetter(c)) hasLetter = true;
            else if (char.IsAsciiDigit(c)) hasDigit = true;
            else return false;
        }
        return hasLetter && hasDigit && trimmed.Length - spaces is >= 4 and <= 9;
    }

    /// <summary>The zippopotam lookup key for a typed postal code: internal
    /// spaces stripped (the indexed codes carry none — GB "M1 1AA" indexes as
    /// "M11"), the US delivery suffix dropped (the -4 of "12345-6789" never
    /// changes the place and the US index is 5-digit only — but NEVER on
    /// other routes: JP's hyphen IS the indexed shape, "100-0001"), and the
    /// GB/CA short form (their indexes are keyed by the 3-char outward code
    /// / FSA — "SW1", "M11", "K1A" — while full codes 404; IM/JE/GG index
    /// under the same crown-FSA scheme). Live-probed against the API
    /// 2026-08: full GB/CA codes 404, the short forms resolve.
    /// </summary>
    private static string NormalizePostalLookup(string code, string routeCountry)
    {
        string compact = code.Replace(" ", string.Empty);
        if (string.Equals(routeCountry, UsRoute, StringComparison.Ordinal) && compact.Length == 10 && compact[5] == '-')
            compact = compact[..5];
        if (ShortFormRoutes.Contains(routeCountry) && compact.Length > 3)
            compact = compact[..3];
        return compact;
    }

    /// <summary>The route countries whose zippopotam index keys on the
    /// short outward code (3 chars) instead of the full postal code.</summary>
    private static readonly HashSet<string> ShortFormRoutes =
    [
        "gb", "ca", "im", "je", "gg",
    ];

    /// <summary>The zippopotam route the postal leg takes for a numeric
    /// code without a country hint (see <see cref="TryPostalRoute"/>).</summary>
    private const string UsRoute = "us";

    /// <summary>
    /// Whether a geocoder candidate bears the query's exact place name —
    /// the ranking's name-exact tier applied to the pick list: the geocoder
    /// returns fuzzy rows alongside the real matches (a live "Springfield"
    /// search carries "Palmyra" and "Jackson"), and those must never enter
    /// the Location Match dropdown — a user who picks one would persist a
    /// place their query did not name. Null (omitted by the geocoder) is
    /// not an exact match.
    /// </summary>
    public static bool IsExactNameMatch(string? candidateName, string namePart)
        => candidateName is not null && EqualsInsensitive(candidateName, namePart);

    /// <summary>
    /// The geocoder search URL — the single URL builder shared by the
    /// resolution flow and the inspector's search-as-you-type (cities and
    /// postal codes both resolve as a name query). <paramref name="query"/>
    /// is the NAME PART only: the comma qualifier is deliberately NOT sent —
    /// the server's qualifier rule gives admin1 abbreviations precedence over
    /// country codes ("Amsterdam, NL" reads as the Canadian province and
    /// returns nothing), while the ranking's client-side suffix tiers (state
    /// table, weak-ISO fallback, aliases) own that disambiguation on purpose.
    /// </summary>
    public static Uri BuildSearchUri(string query, string? countryCode)
    {
        // count=100 is the geocoder's maximum (101 is a 400). The top-10
        // default hid the user's city from BOTH the ranking and the pick list
        // whenever a same-named city ranked >10th by population (live probed
        // 2026-08: "Springfield" + count=10 omits Springfield, Oregon, a
        // 60k city — the user's city was unpickable and the query resolved a
        // different Springfield or none). The server applies the
        // country/admin filters BEFORE the limit, so this only widens the
        // same-name set the client ranking and the Location Match dropdown
        // see.
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=100&language=en&format=json";
        if (!string.IsNullOrWhiteSpace(countryCode))
            url += $"&countryCode={Uri.EscapeDataString(countryCode.Trim())}";
        return new Uri(url);
    }

    /// <summary>
    /// The zippopotam lookup URL. zippopotam routes by country code: the hint
    /// selects the route ("/de/" for a German ZIP — "10115" is both Berlin's
    /// district and a Manhattan ZIP, so the US default would resolve the wrong
    /// country). Unsupported countries 404 and the caller falls back to the
    /// Open-Meteo geocoder.
    /// </summary>
    public static Uri BuildZipLookupUri(string zipCode, string? countryCode)
    {
        string country = string.IsNullOrWhiteSpace(countryCode)
            ? "us"
            : countryCode.Trim().ToLowerInvariant();
        // The country segment is user-configurable widget input — escape it
        // like the ZIP, so a value with '/', '?' or '#' cannot rewrite the
        // request path (a crafted value would otherwise throw or route to a
        // different endpoint and silently fall back to the geocoder).
        return new Uri($"https://api.zippopotam.us/{Uri.EscapeDataString(country)}/{Uri.EscapeDataString(zipCode)}");
    }

    /// <summary>
    /// The Open-Meteo forecast URL for resolved coordinates — the single
    /// builder for the fetch leg. BOTH coordinates are formatted with
    /// <see cref="CultureInfo.InvariantCulture"/> (F4): a comma-decimal OS
    /// locale must never interpolate "40,7100" into the query, or the API
    /// rejects (or mis-reads) the request. The field list is the fetch's one
    /// spelling — the parse side keys off the same field names.
    /// </summary>
    public static Uri BuildForecastUri(double lat, double lon)
    {
        string url = "https://api.open-meteo.com/v1/forecast"
            + $"?latitude={lat.ToString("F4", CultureInfo.InvariantCulture)}"
            + $"&longitude={lon.ToString("F4", CultureInfo.InvariantCulture)}"
            + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,is_day,precipitation,cloud_cover"
            + "&hourly=temperature_2m,relative_humidity_2m,weather_code"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
            + "&timezone=auto";
        return new Uri(url);
    }

    /// <summary>"Name, Admin1, Country" (omitting missing parts) so the widget
    /// title shows exactly which place was picked. The name falls back to the
    /// query only when the geocoder omitted it entirely (missing or null),
    /// never for an explicitly empty string.</summary>
    public static string ComposeLabel(Candidate candidate, string fallbackName)
    {
        string name = candidate.Name ?? fallbackName;

        if (string.IsNullOrWhiteSpace(candidate.Admin1)) return string.IsNullOrWhiteSpace(candidate.Country) ? name : $"{name}, {candidate.Country}";
        return string.IsNullOrWhiteSpace(candidate.Country) ? $"{name}, {candidate.Admin1}" : $"{name}, {candidate.Admin1}, {candidate.Country}";
    }

    /// <summary>The ZIP leg's display label: "City, State" from zippopotam's
    /// place-name and state parts, composing only the non-empty parts — a
    /// response that omits the place name must not produce a ", Texas"
    /// label. The sibling of <see cref="ComposeLabel"/> in the label family.</summary>
    public static string ComposeZipLabel(string city, string state)
    {
        string cityTrim = city.Trim();
        string stateTrim = state.Trim();
        if (cityTrim.Length == 0) return stateTrim;
        if (stateTrim.Length == 0) return cityTrim;
        return $"{cityTrim}, {stateTrim}";
    }

    /// <summary>The display compose for a resolved coordinate identity:
    /// "lat, lon" with two invariant decimals. The one spelling shared by
    /// the resolution legs, the cache-load naming, and the tests.</summary>
    public static string FormatCoordinates(double lat, double lon)
        => $"{lat.ToString("F2", CultureInfo.InvariantCulture)}, {lon.ToString("F2", CultureInfo.InvariantCulture)}";

    /// <summary>A coordinate pair is usable only when both values are finite
    /// and inside the lat/lon ranges (|lat| ≤ 90, |lon| ≤ 180). The range
    /// check exists because "NaN"/"Infinity" PARSE as valid doubles — a
    /// non-finite coordinate would silently poison every downstream URL.</summary>
    public static bool IsValidCoordinate(double lat, double lon)
        => !double.IsNaN(lat) && !double.IsNaN(lon)
            && !double.IsInfinity(lat) && !double.IsInfinity(lon)
            && Math.Abs(lat) <= 90
            && Math.Abs(lon) <= 180;

    /// <summary>
    /// Parses a "lat,lon" location query into its two components;
    /// false when the query is not a two-part coordinate pair OR the values
    /// are not usable coordinates (see <see cref="IsValidCoordinate"/>).
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

    /// <summary>
    /// Resolves a candidate set to one place. The Open-Meteo geocoding API
    /// ranks by population, so a bare name can resolve to the wrong same-named
    /// city worldwide ("Victoria" -> Vitoria, Brazil). The ranking used here:
    /// exact name match first, then a comma-suffix match ("Springfield, MA" /
    /// "Victoria, BC" / "San Jose, Costa Rica") against admin1/country/
    /// country_code, then the country-code hint. A tie at the top score is
    /// deliberately left unresolved — <see cref="ResolveResult.Ambiguous"/>
    /// blocks the fetch until the user picks from the candidates; population
    /// never decides the winner EXCEPT the one same-country carve-out
    /// described in <see cref="Resolve"/> (a tie the suffix pinned to one
    /// country — an untrusted population-decided cross-country tie would show
    /// wrong-city weather). The resolved label carries "Name, Admin1,
    /// Country" so the widget title shows what was picked.
    /// </summary>
    public static ResolveResult Resolve(IReadOnlyList<Candidate> candidates, string namePart, string? suffixPart, string? countryCode, string? locationMatch)
    {
        if (candidates.Count == 0) return new ResolveResult.NoMatch();

        // The state-abbreviation tier's weak ISO fallback is response-aware:
        // a code match gets the weak bonus only when NO candidate carries the
        // state's full admin1 name (the geocoder's top-10 omitted it). When
        // the state reading IS present, it dominates and the code reading is
        // suppressed entirely ("London, CA" with California in the response
        // must never resolve Ontario). Computed before the pick promotion and
        // the ranking so both use the same view.
        var presentStateNames = candidates
            .Select(c => NormalizeForMatch(c.Admin1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A persisted Location Match pick must survive restart/import:
        // candidates are in-memory per instance, so after re-creation the
        // stored pick cannot resolve from cache. If the pick matches a freshly
        // geocoded candidate, promote that candidate to the winner instead of
        // silently reverting to the ranking. The pick resolves a tie
        // deterministically — it runs before the ambiguity gate, so a picked
        // place never reads ambiguous. The promotion requires the picked place
        // to BEAR THE NAME THE QUERY TYPED (the dropdown offers only its
        // exact-name candidates, so the pick obeys the same rule through the
        // same predicate): the live geocoder is fuzzy, so its candidate set
        // can carry places the query did not name ("Vitória" inside a
        // "Victoria" search) — a pick of such a row persisted from before the
        // pick list learned the exact-name rule must not outlive the query
        // that suggested it: the ranking re-decides for the city the user
        // typed, or the wrong-city weather returns on every restart.
        if (!string.IsNullOrWhiteSpace(locationMatch))
        {
            string trimmedPick = locationMatch.Trim();
            var picked = candidates.FirstOrDefault(c =>
                ComposeLabel(c, namePart).Equals(trimmedPick, StringComparison.OrdinalIgnoreCase)
                && IsExactNameMatch(c.Name, namePart));
            // The pick is promoted ONLY when it is consistent with the
            // current query: a suffix the picked candidate does not satisfy
            // ("Springfield, IL" + a persisted "Springfield, Massachusetts"
            // pick) means the user narrowed the query — the ranking (with the
            // IL bonus) must win, or the stale pick silently overrides the
            // explicit suffix and shows wrong-city weather. The country hint
            // is checked the same way: a pick from a different country than
            // the current CountryCode hint is stale. A query WITHOUT a suffix
            // keeps the restart/import promotion (the pick IS the user's last
            // explicit choice).
            if (picked is not null
                && (string.IsNullOrWhiteSpace(suffixPart) || ScoreSuffixMatch(picked, suffixPart, presentStateNames) > 0)
                && (string.IsNullOrWhiteSpace(countryCode) || EqualsInsensitive(picked.CountryCode, countryCode)))
            {
                return new ResolveResult.Resolved(picked.Lat, picked.Lon, ComposeLabel(picked, namePart), picked.Population);
            }
        }

        // Rank: collect (score, population, candidate) and detect a
        // population-decided tie — when more than one candidate shares the
        // top score, the winner is untrustworthy without a pick (the "Berlin"
        // problem). The widget must not display wrong-city weather, so the
        // ambiguity gate reports the tie and the caller leaves coordinates
        // unresolved. A single top scorer is the unambiguous winner —
        // population no longer decides anything, EXCEPT one narrow case: a
        // tie where every candidate is in the SAME country (the geocoder
        // lists the capital plus same-named towns — "Accra, Ghana" ties
        // across two GH entries). The suffix already pinned the country, so
        // the tie cannot pick a wrong country; the highest-population entry
        // is the place.
        var ranked = candidates
            .Select(c => (Candidate: c, Rank: RankGeocodeCandidate(c, namePart, suffixPart, countryCode, presentStateNames)))
            .ToList();
        int bestScore = ranked.Max(r => r.Rank);
        var topTied = ranked.Where(r => r.Rank == bestScore).ToList();
        if (topTied.Count > 1)
        {
            // The population tiebreak applies only when the suffix actually
            // pinned the place: every tied candidate must have matched the
            // suffix (all-or-nothing, so they share the intended country) AND
            // share one country_code. A bare-name tie ("Berlin" + US hint) or
            // a tie where the suffix matched NOBODY ("Washington, District of
            // Columbia" — the DC candidate's name differs, so the state
            // Washingtons tie at the bare score) stays gated.
            string tieCountry = topTied[0].Candidate.CountryCode;
            bool sameCountryTie = !string.IsNullOrWhiteSpace(tieCountry)
                && topTied.All(t => t.Candidate.CountryCode.Equals(tieCountry, StringComparison.OrdinalIgnoreCase))
                && topTied.All(t => ScoreSuffixMatch(t.Candidate, suffixPart, presentStateNames) > 0);
            if (sameCountryTie)
            {
                var best = topTied
                    .Select(t => (Candidate: t.Candidate, Population: t.Candidate.Population))
                    .OrderByDescending(x => x.Population)
                    .First();
                if (best.Population > 0)
                {
                    return new ResolveResult.Resolved(best.Candidate.Lat, best.Candidate.Lon, ComposeLabel(best.Candidate, namePart), best.Population);
                }
            }
            return new ResolveResult.Ambiguous();
        }

        Candidate winner = topTied[0].Candidate;
        return new ResolveResult.Resolved(winner.Lat, winner.Lon, ComposeLabel(winner, namePart), winner.Population);
    }

    /// <summary>The ranking weights. The tiers are deliberately spaced so a
    /// name-exact match always dominates every suffix/hint combination; the
    /// state-code tier shares the exact-equality weight because a US state
    /// code is as precise an identifier as the full name it maps to.</summary>
    private const int NameExactBonus = 1000;
    private const int SuffixExactBonus = 500;
    private const int SuffixPrefixBonus = 250;
    private const int SuffixWeakBonus = 125;
    private const int CountryHintBonus = 500;

    /// <summary>
    /// Pure geocode-candidate ranking: exact name match dominates; the
    /// comma-suffix (state/country) and the country-code hint add weighted
    /// matches. Returns the score only — the caller deliberately leaves a tie
    /// at the top score unresolved (the ambiguity gate), so population never
    /// decides the winner outside the same-country carve-out in
    /// <see cref="Resolve"/>.
    /// </summary>
    private static int RankGeocodeCandidate(Candidate candidate, string namePart, string? suffixPart, string? countryCode, IReadOnlySet<string> presentStateNames)
    {
        string name = candidate.Name ?? "";

        return ScoreExactName(name, namePart)
            + ScoreSuffixMatch(candidate, suffixPart, presentStateNames)
            + ScoreCountryHint(candidate.CountryCode, candidate.Country, countryCode);
    }

    private static int ScoreExactName(string name, string namePart)
        => EqualsInsensitive(name, namePart) ? NameExactBonus : 0;

    private static int ScoreSuffixMatch(Candidate candidate, string? suffixPart, IReadOnlySet<string> presentStateNames)
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
            // A US state abbreviation is a state code, not a country code:
            // it must match admin1 by full name at FULL strength, and a bare
            // ISO-code equality for the same letters must NOT outrank it
            // ("London, CA" — London Ontario's code 'CA' must not beat London
            // California; 'MA' must not resolve to a Moroccan town). Country-
            // code suffixes still work for non-state abbreviations
            // ("San Jose, CR"). The all-or-nothing rule applies here too: a
            // state-code component that matches no admin1 fails the whole
            // suffix, exactly like every other tier — EXCEPT the weak ISO
            // fallback below, which keeps country-code readings alive when a
            // state abbreviation collides with a real ISO code ("Amsterdam,
            // NL" is the Netherlands, not Newfoundland; "Bratislava, SK" is
            // Slovakia, not Saskatchewan): a state match (500) still
            // dominates a code match (125), so "London, CA" keeps resolving
            // to California.
            if (StateAbbreviations.TryGetValue(component, out string? stateFullName))
            {
                if (EqualsInsensitive(candidate.Admin1, stateFullName))
                {
                    score += SuffixExactBonus;
                    continue;
                }
                // Weak ISO fallback: the component is also a real country
                // code — a candidate whose CODE matches keeps a weak score
                // instead of failing the whole suffix — but ONLY when the
                // state's own candidate is absent from the response (the
                // geocoder's top-10 omitted it): "Amsterdam, NL" resolves the
                // Netherlands because no Newfoundland candidate is listed,
                // while "London, CA" with California present still suppresses
                // Ontario's code reading entirely.
                if (EqualsInsensitive(candidate.CountryCode, component)
                    && !presentStateNames.Contains(NormalizeForMatch(stateFullName)))
                {
                    score += SuffixWeakBonus;
                    continue;
                }
                return 0;
            }
            if (EqualsAny(candidate.Admin1, candidate.Country, candidate.CountryCode, component)) score += SuffixExactBonus;
            else if (StartsWithAny(candidate.Admin1, candidate.Country, candidate.CountryCode, component)) score += SuffixPrefixBonus;
            // The renamed-country tier: the geocoder reports official names
            // ("The Netherlands", "Republic of Türkiye") while users type the
            // common English name ("Netherlands", "Turkey"). Contains is the
            // lowest tier — it only breaks a tie between a renamed-country
            // candidate and same-named places elsewhere; the all-or-nothing
            // rule above still requires every component to match at some tier.
            // Components shorter than 4 chars skip contains: a 2-letter code
            // like "PR" must never substring-match "Province".
            else if (component.Length >= 4 && ContainsAny(candidate.Admin1, candidate.Country, candidate.CountryCode, component)) score += SuffixWeakBonus;
            // A renamed-country that even contains-matching cannot reach —
            // the letters differ ("Türkiye" vs "Turkey", "Cabo Verde" vs
            // "Cape Verde") — resolves through the alias table.
            else if (AliasMatches(candidate, component)) score += SuffixWeakBonus;
            // Abbreviation tier: "Springfield, MA", "Houston, TX", "Victoria,
            // BC", "London, UK". The geocoder's jurisdiction names are neither
            // equal to nor prefixed by the abbreviation, and the contains tier
            // is skipped below 4 chars — so without this tier those common
            // queries would tie at the bare-name score and hit the ambiguity
            // gate. Two routes: a state/province abbreviation table (the
            // one-word "Massachusetts" has no useful initials) and the
            // multi-word initials ("BC" of "British Columbia", "UK" of
            // "United Kingdom", "DC" of "District of Columbia").
            else if (StateAbbreviationMatches(candidate, component)
                     || InitialsMatch(candidate.Admin1, component)
                     || InitialsMatch(candidate.Country, component)) score += SuffixWeakBonus;
            else return 0;
        }
        // The total is capped at one exact tier: the tier spacing promises an
        // exact name match dominates EVERY suffix/hint combination, and a
        // three-component suffix would otherwise let a non-exact-name row
        // (500 × 3) beat an exact-name row that failed the all-or-nothing
        // suffix — ungated, because the ambiguity gate only saves a tie, not
        // a beat.
        return Math.Min(score, SuffixExactBonus);
    }

    private static bool EqualsAny(string admin1, string country, string code, string component)
        => EqualsInsensitive(admin1, component)
            || EqualsInsensitive(country, component)
            || EqualsInsensitive(code, component);

    private static bool StartsWithAny(string admin1, string country, string code, string component)
        => StartsWithInsensitive(admin1, component)
            || StartsWithInsensitive(country, component)
            || StartsWithInsensitive(code, component);

    private static bool ContainsAny(string admin1, string country, string code, string component)
        => ContainsInsensitive(admin1, component)
            || ContainsInsensitive(country, component)
            || ContainsInsensitive(code, component);

    /// <summary>Diacritic-insensitive comparison: the user's ASCII spelling
    /// ("Asuncion", "Bogota", "Sao Paulo") must match the geocoder's accented
    /// names ("Asunción", "Bogotá", "São Paulo") — otherwise the exact-name
    /// bonus goes to a same-named ASCII town elsewhere and the accented
    /// capital never wins. Normalization strips combining marks (FormD) and
    /// periods ("St. George's" must match "St George's" — the geocoder
    /// punctuates the Grenada capital differently than the user types).</summary>
    private static string NormalizeForMatch(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (c != '.' && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static bool EqualsInsensitive(string a, string b)
        => NormalizeForMatch(a).Equals(NormalizeForMatch(b), StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithInsensitive(string a, string b)
        => NormalizeForMatch(a).StartsWith(NormalizeForMatch(b), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsInsensitive(string a, string b)
        => NormalizeForMatch(a).Contains(NormalizeForMatch(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Common English country/territory names the geocoder reports
    /// under an official/renamed name whose letters differ (the contains tier
    /// cannot reach these) — the user's spelling is matched against the
    /// aliased form of each candidate's country. US-territory entries often
    /// carry an EMPTY country field with only the code ("San Juan" is PR with
    /// no country), so the alias resolves to the code as well.</summary>
    private static readonly Dictionary<string, string> CountryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Turkey"] = "Türkiye",
        ["Cape Verde"] = "Cabo Verde",
        ["Czech Republic"] = "Czechia",
        // The user's short form "Congo Republic" maps to the geocoder's
        // official "Republic of the Congo" (the old entry was the reverse —
        // a suffix "Congo Republic" never matched any candidate field).
        ["Congo Republic"] = "Republic of the Congo",
        // Trailing country designators in "City, State, USA" suffixes: the
        // all-or-nothing rule would otherwise gate the whole query on a
        // component neither equals nor contains "United States". Both
        // spellings are keys — the normalized lookup ("US") and the raw
        // dotted form ("U.S.").
        ["USA"] = "United States",
        ["US"] = "United States",
        ["U.S."] = "United States",
        ["Puerto Rico"] = "PR",
        ["Guam"] = "GU",
        ["US Virgin Islands"] = "VI",
        ["American Samoa"] = "AS",
        ["Northern Mariana Islands"] = "MP",
    };

    private static bool AliasMatches(Candidate candidate, string component)
    {
        // The lookup tries the raw AND the normalized form: "U.S." matches
        // the dotted key directly, "US" reaches it via the dot-stripped key
        // (normalization strips periods, so the dotted key alone would miss).
        if (!CountryAliases.TryGetValue(component, out string? official)
            && !CountryAliases.TryGetValue(NormalizeForMatch(component), out official))
        {
            return false;
        }
        if (EqualsInsensitive(candidate.Admin1, official)
            || EqualsInsensitive(candidate.Country, official)
            || EqualsInsensitive(candidate.CountryCode, official))
        {
            return true;
        }
        // Contains only for real names — a short alias value ("PR") must
        // never substring-match "Province".
        return official.Length >= 4
            && (ContainsInsensitive(candidate.Admin1, official)
                || ContainsInsensitive(candidate.Country, official)
                || ContainsInsensitive(candidate.CountryCode, official));
    }

    /// <summary>US state + DC abbreviations the geocoder reports by full name
    /// ("Massachusetts" has no initials a 2-letter suffix could match). The
    /// component is matched case-insensitively against the abbreviation, then
    /// the full name against the candidate's admin1.</summary>
    private static readonly Dictionary<string, string> StateAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama",
        ["AK"] = "Alaska",
        ["AZ"] = "Arizona",
        ["AR"] = "Arkansas",
        ["CA"] = "California",
        ["CO"] = "Colorado",
        ["CT"] = "Connecticut",
        ["DE"] = "Delaware",
        ["FL"] = "Florida",
        ["GA"] = "Georgia",
        ["HI"] = "Hawaii",
        ["ID"] = "Idaho",
        ["IL"] = "Illinois",
        ["IN"] = "Indiana",
        ["IA"] = "Iowa",
        ["KS"] = "Kansas",
        ["KY"] = "Kentucky",
        ["LA"] = "Louisiana",
        ["ME"] = "Maine",
        ["MD"] = "Maryland",
        ["MA"] = "Massachusetts",
        ["MI"] = "Michigan",
        ["MN"] = "Minnesota",
        ["MS"] = "Mississippi",
        ["MO"] = "Missouri",
        ["MT"] = "Montana",
        ["NE"] = "Nebraska",
        ["NV"] = "Nevada",
        ["NH"] = "New Hampshire",
        ["NJ"] = "New Jersey",
        ["NM"] = "New Mexico",
        ["NY"] = "New York",
        ["NC"] = "North Carolina",
        ["ND"] = "North Dakota",
        ["OH"] = "Ohio",
        ["OK"] = "Oklahoma",
        ["OR"] = "Oregon",
        ["PA"] = "Pennsylvania",
        ["RI"] = "Rhode Island",
        ["SC"] = "South Carolina",
        ["SD"] = "South Dakota",
        ["TN"] = "Tennessee",
        ["TX"] = "Texas",
        ["UT"] = "Utah",
        ["VT"] = "Vermont",
        ["VA"] = "Virginia",
        ["WA"] = "Washington",
        ["WV"] = "West Virginia",
        ["WI"] = "Wisconsin",
        ["WY"] = "Wyoming",
        ["DC"] = "District of Columbia",
        // Canadian provinces: one-word names ("Quebec", "Ontario") have no
        // usable initials, so their codes need the same table treatment as
        // the US states ("Montreal, QC" must not hit the ambiguity gate).
        ["QC"] = "Quebec",
        ["ON"] = "Ontario",
        ["AB"] = "Alberta",
        ["MB"] = "Manitoba",
        ["NB"] = "New Brunswick",
        ["NL"] = "Newfoundland and Labrador",
        ["NS"] = "Nova Scotia",
        ["NT"] = "Northwest Territories",
        ["NU"] = "Nunavut",
        ["PE"] = "Prince Edward Island",
        ["SK"] = "Saskatchewan",
        ["YT"] = "Yukon",
    };

    private static bool StateAbbreviationMatches(Candidate candidate, string component)
        => StateAbbreviations.TryGetValue(component, out string? fullName)
           && EqualsInsensitive(candidate.Admin1, fullName);

    /// <summary>Multi-word jurisdiction initials: "BC" of "British Columbia",
    /// "UK" of "United Kingdom", "DC" of "District of Columbia". One-word
    /// names ("Massachusetts") have no usable initials and never match here —
    /// they route through <see cref="StateAbbreviations"/> instead.</summary>
    private static bool InitialsMatch(string jurisdiction, string component)
    {
        if (string.IsNullOrWhiteSpace(jurisdiction)) return false;
        string[] words = NormalizeForMatch(jurisdiction).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;
        var initials = new StringBuilder(words.Length);
        foreach (string word in words)
        {
            // Skip stop-word-length tokens ("of", "de", "la"): "District of
            // Columbia" must initial to "DC", never "DOC".
            if (word.Length <= 2) continue;
            initials.Append(char.ToUpperInvariant(word[0]));
        }
        return initials.Length > 0 && initials.ToString().Equals(component, StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreCountryHint(string code, string country, string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return 0;
        string hint = countryCode.Trim();
        return EqualsInsensitive(code, hint)
            || EqualsInsensitive(country, hint)
            ? CountryHintBonus
            : 0;
    }
}
