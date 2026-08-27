namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherLocationResolverTests
{
    private static WeatherLocationResolver.Candidate C(string admin1, string country, string code, double population = 0, string name = "Springfield")
        => new(name, admin1, country, code, 0, 0, population);

    // ── state/province abbreviation tier ───────────────────

    [TestMethod]
    public void Resolve_StateAbbreviation_BreaksTieByFullName()
    {
        // Missouri starts "Mi" — the abbreviation tier is the only route for
        // "Springfield, MO" (the MA case worked by accidental StartsWith).
        // The winner deliberately has the LOWER population: the rule, not
        // population, must decide.
        var result = WeatherLocationResolver.Resolve(
            [C("Missouri", "United States", "US", 100), C("Massachusetts", "United States", "US", 167601)],
            "Springfield", "MO", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Missouri, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_StateAbbreviation_MatchesAnyAdministrativeName()
    {
        // Arizona starts "Ar" — "Springfield, AZ" must not hit the ambiguity gate.
        var result = WeatherLocationResolver.Resolve(
            [C("Arizona", "United States", "US", 100), C("Arkansas", "United States", "US", 200)],
            "Springfield", "AZ", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        Assert.AreEqual("Springfield, Arizona, United States", ((WeatherLocationResolver.ResolveResult.Resolved)result).Label);
    }

    [TestMethod]
    public void Resolve_MultiWordInitials_BreaksTie()
    {
        // "Victoria, BC" — the initials of "British Columbia", not a state code.
        var result = WeatherLocationResolver.Resolve(
            [C("British Columbia", "Canada", "CA", 100, "Victoria"), C("Texas", "United States", "US", 999, "Victoria")],
            "Victoria", "BC", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Victoria, British Columbia, Canada", resolved.Label);
    }

    [TestMethod]
    public void Resolve_CountryInitials_BreaksTie()
    {
        // "London, UK" — the initials of "United Kingdom" beat population.
        var result = WeatherLocationResolver.Resolve(
            [C("Ontario", "Canada", "CA", 366151, "London"), C("", "United Kingdom", "GB", 500000, "London")],
            "London", "UK", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("London, United Kingdom", resolved.Label);
    }

    [TestMethod]
    public void Resolve_DistrictInitials_MatchesDistrictOfColumbia()
    {
        // "Washington, DC" — initials of the multi-word district; the PA
        // Washington (exact name, no suffix) must lose to the district.
        var result = WeatherLocationResolver.Resolve(
            [C("District of Columbia", "United States", "US", 689545, "Washington"), C("Pennsylvania", "United States", "US", 13176, "Washington")],
            "Washington", "DC", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Washington, District of Columbia, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_UnpairedSurrogateInSuffix_DegradesWithoutThrowing()
    {
        // A hand-edited profile can smuggle a lone \uXXXX escape into the
        // location field; NormalizeForMatch degrades to the raw value
        // instead of throwing ArgumentException out of the resolution.
        var result = WeatherLocationResolver.Resolve(
            [C("Missouri", "United States", "US", 100)],
            "Springfield", "MO\uD800", null, null);

        Assert.IsNotNull(result, "a bad query component must resolve, not throw");
    }

    [TestMethod]
    public void Resolve_Abbreviation_DoesNotMatchUnrelatedJurisdiction()
    {
        // "TX" must not resolve a Texas candidate whose admin1 is "Texas" via
        // a DIFFERENT state's abbreviation — and a non-matching abbreviation
        // must leave the tie gated rather than pick by population.
        var result = WeatherLocationResolver.Resolve(
            [C("Texas", "United States", "US", 999), C("Massachusetts", "United States", "US", 100)],
            "Springfield", "TX", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Texas, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_UnknownAbbreviation_StaysAmbiguous()
    {
        // An abbreviation no candidate satisfies must not silently pick by
        // population — the gate holds.
        var result = WeatherLocationResolver.Resolve(
            [C("Texas", "United States", "US", 999), C("Massachusetts", "United States", "US", 100)],
            "Springfield", "ZZ", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Ambiguous));
    }

    [TestMethod]
    public void BuildZipLookupUri_EscapesCountrySegment()
    {
        var uri = WeatherLocationResolver.BuildZipLookupUri("10115", "de");
        Assert.AreEqual("https://api.zippopotam.us/de/10115", uri.AbsoluteUri);

        // A hostile country value must be fully escaped into the path — every
        // query-injection character ('/', '?', '&', '=') stays percent-encoded
        // so the value can never rewrite the request.
        var hostile = WeatherLocationResolver.BuildZipLookupUri("10115", "us/evil?x=1&y=2");
        Assert.AreEqual("https://api.zippopotam.us/us%2Fevil%3Fx%3D1%26y%3D2/10115", hostile.AbsoluteUri,
            "the country segment must be a single, fully-escaped path segment");
    }

    // ── state-code vs ISO-code collision ──────────────────

    [TestMethod]
    public void Resolve_StateCodeCollision_StateWinsOverBareCodeEquality()
    {
        // "London, CA": Ontario's code 'CA' must NOT outrank California's
        // admin1 via the state-abbreviation tier (a 2-letter code collision
        // would silently return wrong-country weather).
        var result = WeatherLocationResolver.Resolve(
            [C("Ontario", "Canada", "CA", 366151, "London"), C("California", "United States", "US", 500000, "London")],
            "London", "CA", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("London, California, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_StateCodeCollision_MaDoesNotResolveToMorocco()
    {
        // "Springfield, MA": a Moroccan candidate whose code is 'MA' must not
        // win over Massachusetts.
        var result = WeatherLocationResolver.Resolve(
            [C("Massachusetts", "United States", "US", 155932), C("", "Morocco", "MA", 500000)],
            "Springfield", "MA", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Massachusetts, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_LondonCA_StatePresentInResponse_StateWinsAndCodeReadingSuppressed()
    {
        // The weak ISO fallback is response-aware: with California present in
        // the response, Ontario's 'CA' code match must NOT earn the weak
        // bonus - the state reading dominates and the code reading is
        // suppressed entirely (Ontario scores 0, California 500). Observable
        // through a persisted pick: "London, Ontario, Canada" is NOT promoted
        // (its suffix score is 0), so the ranking decides and California
        // wins - the pre-fix code granted Ontario the weak 125 and would have
        // promoted the pick to the wrong country.
        var result = WeatherLocationResolver.Resolve(
            [C("Ontario", "Canada", "CA", 366151, "London"), C("California", "United States", "US", 500000, "London")],
            "London", "CA", null, "London, Ontario, Canada");

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("London, California, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_LondonCA_StateAbsentFromResponse_WeakCodeFallbackWins()
    {
        // The weak ISO fallback: with NO California candidate in the response
        // (the geocoder's top-10 omitted it), Ontario's 'CA' code match keeps
        // the weak bonus (125) and wins over same-named candidates that fail
        // the suffix - the code reading is the only reading left.
        var result = WeatherLocationResolver.Resolve(
            [C("Ontario", "Canada", "CA", 366151, "London"), C("Ohio", "United States", "US", 1000, "London")],
            "London", "CA", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("London, Ontario, Canada", resolved.Label);
    }

    [TestMethod]
    public void Resolve_AmsterdamNL_StateAbsentFromResponse_WeakCodeFallbackApplies()
    {
        // "Amsterdam, NL": NL is Newfoundland's abbreviation AND the
        // Netherlands' ISO code. With no Newfoundland candidate in the
        // response, the Netherlands' code match earns the weak bonus and
        // beats the same-named US towns.
        var result = WeatherLocationResolver.Resolve(
            [C("North Holland", "The Netherlands", "NL", 741636, "Amsterdam"),
             C("New York", "United States", "US", 18620, "Amsterdam"),
             C("Ohio", "United States", "US", 510, "Amsterdam")],
            "Amsterdam", "NL", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Amsterdam, North Holland, The Netherlands", resolved.Label);
    }

    [TestMethod]
    public void Resolve_NonStateCountryCode_StillMatchesByCode()
    {
        // "San Jose, CR" — 'CR' is not a US state abbreviation, so the
        // country-code equality must still select Costa Rica.
        var result = WeatherLocationResolver.Resolve(
            [C("California", "United States", "US", 1026908, "San Jose"), C("San José Province", "Costa Rica", "CR", 335007, "San Jose")],
            "San Jose", "CR", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("San Jose, San José Province, Costa Rica", resolved.Label);
    }

    [TestMethod]
    public void Resolve_TrailingCountryDesignator_DoesNotGateTheQuery()
    {
        // "Springfield, MA, USA": 'USA' is a common trailing designator —
        // the all-or-nothing suffix rule must not collapse the whole query.
        var result = WeatherLocationResolver.Resolve(
            [C("Massachusetts", "United States", "US", 155932), C("Missouri", "United States", "US", 167601)],
            "Springfield", "MA, USA", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Massachusetts, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_ShortAliasValue_NeverSubstringMatches()
    {
        // "San Juan, Puerto Rico": the alias resolves to the 2-letter "PR"
        // value, whose contains tier is skipped — the Dominican "San Juan
        // Province" must NOT match "PR" as a substring, or a wrong country's
        // weather would display for a Puerto Rico query (the tie then gates
        // until the user picks).
        var result = WeatherLocationResolver.Resolve(
            [
                new WeatherLocationResolver.Candidate("San Juan", "San Juan Province", "Dominican Republic", "DO", 0, 0, 72950),
                new WeatherLocationResolver.Candidate("San Juan", "Texas", "United States", "US", 0, 0, 36556)
            ],
            "San Juan", "Puerto Rico", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Ambiguous),
            "no candidate may win via a substring match of a short alias value");
    }

    [TestMethod]
    public void Resolve_DottedAliasLookup_MatchesTheRawDottedKey()
    {
        // "Springfield, U.S." — the raw dotted form must hit the alias table
        // directly (normalization strips periods, so the dotted key is the
        // only route): the US candidate wins over the Canadian one.
        var result = WeatherLocationResolver.Resolve(
            [
                new WeatherLocationResolver.Candidate("Springfield", "Massachusetts", "United States", "US", 0, 0, 155932),
                new WeatherLocationResolver.Candidate("Springfield", "Quebec", "Canada", "CA", 0, 0, 999)
            ],
            "Springfield", "U.S.", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Massachusetts, United States", resolved.Label);
    }

    // ── core Resolve branches ─────────────────────────────

    [TestMethod]
    public void Resolve_NoCandidates_ReturnsNoMatch()
    {
        var result = WeatherLocationResolver.Resolve([], "Berlin", null, null, null);
        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.NoMatch));
    }

    [TestMethod]
    public void Resolve_SingleExactName_NoSuffixOrHint_Wins()
    {
        var result = WeatherLocationResolver.Resolve(
            [C("Berlin", "Germany", "DE", 3600000, "Berlin"), C("Ohio", "United States", "US", 1000, "Berlin Township")],
            "Berlin", null, null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Berlin, Berlin, Germany", resolved.Label);
    }

    [TestMethod]
    public void Resolve_SameCountryTie_PopulationBreaksIt()
    {
        // The Accra/Asunción case: same-country + suffix-pinned tie → the
        // highest population wins.
        var result = WeatherLocationResolver.Resolve(
            [C("Greater Accra", "Ghana", "GH", 100000, "Accra"), C("Greater Accra", "Ghana", "GH", 200000, "Accra")],
            "Accra", "Greater Accra", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual(200000, resolved.Population);
    }

    [TestMethod]
    public void Resolve_LocationMatchPick_PromotesThePickedCandidate()
    {
        // A persisted pick must survive re-geocoding: the picked candidate is
        // promoted over the ranking (which would pick the other one).
        var result = WeatherLocationResolver.Resolve(
            [C("California", "United States", "US", 999999, "Springfield"), C("Massachusetts", "United States", "US", 100, "Springfield")],
            "Springfield", null, null, "Springfield, Massachusetts, United States");

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Massachusetts, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_LocationMatchPick_CountryCodeHintDisagrees_IsStaleAndNotPromoted()
    {
        // A persisted pick from a different country than the current
        // CountryCode hint is stale: the user narrowed the query with a
        // country hint, so the ranking (with its country-hint bonus) must win
        // - the stale pick must not silently override the explicit hint.
        var result = WeatherLocationResolver.Resolve(
            [C("California", "United States", "US", 999999, "Springfield"), C("Ontario", "Canada", "CA", 100, "Springfield")],
            "Springfield", null, "US", "Springfield, Ontario, Canada");

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, California, United States", resolved.Label);
    }

    [TestMethod]
    public void Resolve_LocationMatchPick_CountryCodeHintMatches_IsPromoted()
    {
        // A pick consistent with the current CountryCode hint is the user's
        // last explicit choice and must survive restart/import: it is
        // promoted over the ranking, which would pick the higher-population
        // same-named town.
        var result = WeatherLocationResolver.Resolve(
            [C("California", "United States", "US", 999999, "Springfield"), C("Ontario", "Canada", "CA", 100, "Springfield")],
            "Springfield", null, "CA", "Springfield, Ontario, Canada");

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Springfield, Ontario, Canada", resolved.Label);
    }

    [TestMethod]
    public void Resolve_LocationMatchPick_FuzzyRowName_NotPromotedAndRankingDecides()
    {
        // A persisted Location Match pick must name the place the query typed:
        // the geocoder's candidate set is fuzzy ("Vitória" inside a "Victoria"
        // search), and a pick of such a row persisted before the pick list
        // learned the exact-name rule must not outlive the query — the ranking
        // re-decides for the city the user typed, or the wrong-city weather
        // returns on every restart/import. The fuzzy row even carries the
        // HIGHER population: the exact-name tier, not population, must win.
        var result = WeatherLocationResolver.Resolve(
            [C("British Columbia", "Canada", "CA", 335696, "Victoria"), C("Espírito Santo", "Brazil", "BR", 1962476, "Vitória")],
            "Victoria", null, null, "Vitória, Espírito Santo, Brazil");

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual("Victoria, British Columbia, Canada", resolved.Label,
            "the fuzzy pick must fall back to the exact-name ranking");
    }

    [TestMethod]
    public void SplitQuery_WithSuffix_SplitsNameAndSuffix()
    {
        var (name, suffix) = WeatherLocationResolver.SplitQuery("Springfield, MA");
        Assert.AreEqual("Springfield", name);
        Assert.AreEqual("MA", suffix);
    }

    [TestMethod]
    public void SplitQuery_NoSuffix_ReturnsNullSuffix()
    {
        var (name, suffix) = WeatherLocationResolver.SplitQuery("Berlin");
        Assert.AreEqual("Berlin", name);
        Assert.IsNull(suffix);
    }

    [TestMethod]
    public void TryPostalRoute_FiveDigit_NoHint_RoutesUs()
    {
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("10115", null, out string lookup, out string route));
        Assert.AreEqual("10115", lookup);
        Assert.AreEqual("us", route, "a numeric code without a hint reads as US — the geocoder's own index is US-biased too");
    }

    [TestMethod]
    public void TryPostalRoute_FiveDigit_Hinted_RoutesHint()
    {
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("10115", "DE", out string lookup, out string route));
        Assert.AreEqual("10115", lookup);
        Assert.AreEqual("de", route);
    }

    [TestMethod]
    public void TryPostalRoute_ZipPlus4_NoHint_FiveDigitLookupOnUsRoute()
    {
        // The US delivery suffix never changes the place and the US zippopotam
        // index is 5-digit only — "12345-6789" looks up as "12345".
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("10001-1234", null, out string lookup, out string route));
        Assert.AreEqual("10001", lookup);
        Assert.AreEqual("us", route);

        // The -4 strip is US-route only: a hinted non-US route keeps the
        // hyphenated code (JP's hyphen IS the indexed shape).
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("10001-1234", "jp", out lookup, out route));
        Assert.AreEqual("10001-1234", lookup);
        Assert.AreEqual("jp", route);
    }

    [TestMethod]
    public void TryPostalRoute_HyphenatedForeignCode_KeptOnForeignRoute()
    {
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("100-0001", "JP", out string lookup, out string route));
        Assert.AreEqual("100-0001", lookup);
        Assert.AreEqual("jp", route);
    }

    [TestMethod]
    public void TryPostalRoute_GbAlphanumeric_ShortFormLookup()
    {
        // zippopotam's GB index keys on the 3-char outward code — the full
        // "M1 1AA" 404s (live-probed), "M11" resolves Manchester.
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("M1 1AA", "GB", out string lookup, out string route));
        Assert.AreEqual("M11", lookup);
        Assert.AreEqual("gb", route);

        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("SW1A 1AA", "gb", out lookup, out _));
        Assert.AreEqual("SW1", lookup);
    }

    [TestMethod]
    public void TryPostalRoute_CaFsa_ShortFormLookup()
    {
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("K1A 0A1", "CA", out string lookup, out string route));
        Assert.AreEqual("K1A", lookup);
        Assert.AreEqual("ca", route);
    }

    [TestMethod]
    public void TryPostalRoute_AlphanumericWithoutHint_NotPostal()
    {
        // An alphanumeric code carries no self-describing route — guessing US
        // would be a coin flip, so the query keeps the (failing-safe) city leg.
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("M1 1AA", null, out _, out _));
    }

    [TestMethod]
    public void TryPostalRoute_CityNames_NotPostal()
    {
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("Paris", null, out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("Springfield, MA", "US", out _, out _), "a comma query is a city query");
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("40.71,-74.00", null, out _, out _), "a coordinate pair is never a postal code");
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("", null, out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("٠١١٥١", null, out _, out _), "Unicode digits must not route into the postal leg");
    }

    [TestMethod]
    public void TryPostalRoute_NumericShapes_Gate()
    {
        // 2–10 bare digits route (US ZIP, DE/FR/ES/IT 5-digit, NO/SE 4-digit,
        // IN 6-digit); 11+ is not a postal code shape.
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("110001", "IN", out _, out _));
        Assert.IsTrue(WeatherLocationResolver.TryPostalRoute("7005", null, out string lookup, out _));
        Assert.AreEqual("7005", lookup);
        Assert.IsFalse(WeatherLocationResolver.TryPostalRoute("12345678901", null, out _, out _));
    }

    [TestMethod]
    public void IsExactNameMatch_Predicate()
    {
        Assert.IsTrue(WeatherLocationResolver.IsExactNameMatch("Springfield", "Springfield"));
        Assert.IsTrue(WeatherLocationResolver.IsExactNameMatch("springfield", "Springfield"), "case-insensitive");
        Assert.IsFalse(WeatherLocationResolver.IsExactNameMatch("Palmyra", "Springfield"), "fuzzy rows are not exact matches");
        Assert.IsFalse(WeatherLocationResolver.IsExactNameMatch("East Springfield", "Springfield"));
        Assert.IsFalse(WeatherLocationResolver.IsExactNameMatch(null, "Springfield"), "a geocoder-omitted name is not an exact match");
    }

    [TestMethod]
    public void BuildSearchUri_AppendsEscapedCountryCode()
    {
        var uri = WeatherLocationResolver.BuildSearchUri("Springfield", "US");
        Assert.IsTrue(uri.AbsoluteUri.Contains("name=Springfield", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(uri.AbsoluteUri.Contains("countryCode=US", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(uri.AbsoluteUri.Contains("count=100", StringComparison.OrdinalIgnoreCase),
            "the geocoder's maximum — the top-10 default hid the user's city from the pick list (Springfield, Oregon absent at count=10)");
    }

    [TestMethod]
    public void ComposeLabel_OmitsMissingParts()
    {
        var full = new WeatherLocationResolver.Candidate("Victoria", "British Columbia", "Canada", "CA", 0, 0, 0);
        var noAdmin = new WeatherLocationResolver.Candidate("London", "", "United Kingdom", "GB", 0, 0, 0);
        var bare = new WeatherLocationResolver.Candidate("Springfield", "", "", "", 0, 0, 0);

        Assert.AreEqual("Victoria, British Columbia, Canada", WeatherLocationResolver.ComposeLabel(full, "Victoria"));
        Assert.AreEqual("London, United Kingdom", WeatherLocationResolver.ComposeLabel(noAdmin, "London"));
        Assert.AreEqual("Springfield", WeatherLocationResolver.ComposeLabel(bare, "Springfield"));
    }

    [TestMethod]
    public void BuildForecastUri_PinsTheExactUrlAndFieldList()
    {
        // The forecast URL is the fetch's ONE spelling - the parse side keys
        // off the same field names, so the field list is pinned character by
        // character (invariant F4 coordinates: 40.7100, never 40.71 or
        // 40,7100; every current/hourly/daily field the parser reads).
        var uri = WeatherLocationResolver.BuildForecastUri(40.71, -74.0);
        Assert.AreEqual(
            "https://api.open-meteo.com/v1/forecast?latitude=40.7100&longitude=-74.0000"
            + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,is_day,precipitation,cloud_cover"
            + "&hourly=temperature_2m,relative_humidity_2m,weather_code"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
            + "&timezone=auto",
            uri.AbsoluteUri);
    }

    // The coordinate validity rules moved here from the geocoder adapter
    // (the adapter is transport + JSON shape only); the pins follow the rules.

    [TestMethod]
    public void TryParseCoordinatePair_ValidPair_Parses()
    {
        Assert.IsTrue(WeatherLocationResolver.TryParseCoordinatePair("52.52, 13.405", out double lat, out double lon));
        Assert.AreEqual(52.52, lat, 0.0001);
        Assert.AreEqual(13.405, lon, 0.0001);
    }

    [TestMethod]
    public void TryParseCoordinatePair_NotAPair_ReturnsFalse()
    {
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("52.52", out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("Berlin", out _, out _));
    }

    [TestMethod]
    public void TryParseCoordinatePair_NonFiniteOrOutOfRange_ReturnsFalse()
    {
        // "NaN" and "Infinity" PARSE as valid doubles — the coordinate
        // validation is what rejects them, along with the range bounds.
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("NaN, 5", out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("5, NaN", out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("Infinity, 5", out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("-Infinity, 5", out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("91, 0", out _, out _), "|lat| > 90 is out of range");
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("-91, 0", out _, out _));
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("45, 181", out _, out _), "|lon| > 180 is out of range");
        Assert.IsFalse(WeatherLocationResolver.TryParseCoordinatePair("45, -181", out _, out _));
        // Boundary values are still valid.
        Assert.IsTrue(WeatherLocationResolver.TryParseCoordinatePair("90, 180", out _, out _));
    }

    [TestMethod]
    public void FormatCoordinates_InvariantTwoDecimals()
    {
        Assert.AreEqual("52.52, 13.41", WeatherLocationResolver.FormatCoordinates(52.52, 13.406));
    }

    [TestMethod]
    public void ComposeZipLabel_ComposesOnlyNonEmptyParts()
    {
        Assert.AreEqual("Austin, Texas", WeatherLocationResolver.ComposeZipLabel("Austin", "Texas"));
        Assert.AreEqual("Texas", WeatherLocationResolver.ComposeZipLabel("", "Texas"), "an omitted place name must not produce a ', Texas' label");
        Assert.AreEqual("Austin", WeatherLocationResolver.ComposeZipLabel("Austin", ""));
        Assert.AreEqual("Austin, Texas", WeatherLocationResolver.ComposeZipLabel("  Austin  ", "  Texas  "), "parts are trimmed before composing");
    }

    // ── suffix tier cap ───────────────────────────────

    [TestMethod]
    public void Resolve_ThreeComponentSuffix_NameExactStillDominates()
    {
        // The tier spacing claims an exact name match dominates EVERY
        // suffix/hint combination: the per-component sum must be capped at
        // one exact tier. Unguarded, a three-component suffix ("City, State,
        // Country, Alias") gives a non-exact-name row 1125 (the alias
        // component scores the weak tier, not the exact) and lets it beat —
        // UNGATED — an exact-name row that failed the all-or-nothing suffix
        // (1000). The gate saves a tie, not a beat.
        var result = WeatherLocationResolver.Resolve(
            [C("Connecticut", "United States", "US", 1000, "Alpha"),
             C("Massachusetts", "United States", "US", 500, "Alpha Gardens")],
            "Alpha", "Massachusetts, United States, USA", null, null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual(1000.0, resolved.Population, "the exact-name row must dominate the capped suffix sum");
        Assert.AreEqual("Alpha, Connecticut, United States", resolved.Label, "the winner is the exact-name row — not a same-population coincidence");
    }

    [TestMethod]
    public void Resolve_CappedSuffixTie_SameCountryPopulationCarveOutDecides()
    {
        // The suffix cap's known tradeoff, pinned: two non-exact-name rows that
        // PRE-CAP were separated by tier granularity (exact-state 500 +
        // country-exact 500 = 1000 vs exact-state 500 + country-prefix 250 = 750)
        // TIE after the cap (500 vs 500). With no exact-name row, the
        // same-country carve-out takes over — population decides, and the
        // loser stays inside the country the suffix pinned. This freezes the
        // post-cap behavior so a future re-tuning of the tiers or the cap
        // flips a pinned test instead of silently re-ranking real queries.
        var result = WeatherLocationResolver.Resolve(
            [C("Massachusetts", "United States", "US", 100, "Alpha City"),
             C("Massachusetts", "United States of America", "US", 9000, "Alpha Gardens")],
            "Alpha", "Massachusetts, United States", "US", null);

        Assert.IsInstanceOfType(result, typeof(WeatherLocationResolver.ResolveResult.Resolved));
        var resolved = (WeatherLocationResolver.ResolveResult.Resolved)result;
        Assert.AreEqual(9000.0, resolved.Population, "the same-country carve-out (population) decides the capped tie — the 100-pop row loses its pre-cap tier lead");
        Assert.AreEqual("Alpha Gardens, Massachusetts, United States of America", resolved.Label);
    }
}
