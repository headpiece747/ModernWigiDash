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
