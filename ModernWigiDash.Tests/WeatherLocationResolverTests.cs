using ModernWigiDash.Widgets;

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
    public void IsZipCode_FiveAsciiDigits_ReturnsTrue() =>
        Assert.IsTrue(WeatherLocationResolver.IsZipCode("10115"));

    [TestMethod]
    public void IsZipCode_NonAsciiDigits_ReturnsFalse()
    {
        Assert.IsFalse(WeatherLocationResolver.IsZipCode("٠١١٥١"), "Unicode digits must not count as a ZIP");
        Assert.IsFalse(WeatherLocationResolver.IsZipCode("1011"), "Four digits are not a ZIP");
    }

    [TestMethod]
    public void BuildSearchUri_AppendsEscapedCountryCode()
    {
        var uri = WeatherLocationResolver.BuildSearchUri("Springfield", "US");
        Assert.IsTrue(uri.AbsoluteUri.Contains("name=Springfield", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(uri.AbsoluteUri.Contains("countryCode=US", StringComparison.OrdinalIgnoreCase));
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
}
