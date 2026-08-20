using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the widget's snapshot-apply policy: the version-then-identity gate
/// and the null-keeps / per-list-version-bump merge that turn a fetched or
/// cached snapshot into the widget's display state. Pure — the module holds
/// no lock and no fields, so the rules are assertable directly; the widget's
/// ApplySnapshot keeps only the gate discipline.
/// </summary>
[TestClass]
public class WeatherSnapshotApplyPolicyTests
{
    private static readonly WeatherSnapshot FullSnapshot = new(
        CurrentTempC: 23.4, FeelsLikeC: 22.1, Humidity: 65.0, WindSpeedKmH: 12.5,
        WeatherCode: 2, HighTempC: 27.0, LowTempC: 18.0,
        DailyForecasts: [new("Mon", 27.0, 18.0, 2)],
        HourlyForecasts: [new("12:00", 24.0, 3)],
        ResolvedCityName: "Victoria, British Columbia, Canada", Lat: 48.4, Lon: -123.3);

    private static WeatherSnapshotState FullState()
        => new()
        {
            DataVersion = 4,
            CurrentTempC = 10.0,
            FeelsLikeC = 9.0,
            Humidity = 80.0,
            WindSpeedKmH = 5.0,
            WeatherCode = 61,
            HighTempC = 15.0,
            LowTempC = 8.0,
            ForecastVersion = 3,
            DailyForecasts = [new("Sun", 15.0, 8.0, 61)],
            HourlyForecasts = [new("09:00", 11.0, 61)],
        };

    // -- GuardsPass: version-then-identity ---------------------------------

    [TestMethod]
    public void GuardsPass_NoExpectedVersionAndNoGuard_Passes()
    {
        Assert.IsTrue(WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion: null, dataVersion: 5, identityGuard: null));
    }

    [TestMethod]
    public void GuardsPass_VersionMatch_WithNoGuard_Passes()
    {
        Assert.IsTrue(WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion: 5, dataVersion: 5, identityGuard: null));
    }

    [TestMethod]
    public void GuardsPass_VersionMismatch_Rejects()
    {
        Assert.IsFalse(WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion: 4, dataVersion: 5, identityGuard: null));
    }

    [TestMethod]
    public void GuardsPass_VersionMismatch_DoesNotEvaluateTheIdentityGuard()
    {
        bool evaluated = false;
        bool result = WeatherSnapshotApplyPolicy.GuardsPass(
            expectedVersion: 4, dataVersion: 5, identityGuard: () => { evaluated = true; return true; });

        Assert.IsFalse(result);
        Assert.IsFalse(evaluated, "a stale apply must short-circuit before the identity predicate runs");
    }

    [TestMethod]
    public void GuardsPass_IdentityGuardFails_Rejects()
    {
        Assert.IsFalse(WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion: 5, dataVersion: 5, identityGuard: () => false));
    }

    [TestMethod]
    public void GuardsPass_IdentityGuardPasses_Accepts()
    {
        Assert.IsTrue(WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion: 5, dataVersion: 5, identityGuard: () => true));
    }

    // -- Merge: null-keeps + per-list version bump -------------------------

    [TestMethod]
    public void Merge_AlwaysBumpsTheDataVersion()
    {
        var next = WeatherSnapshotApplyPolicy.Merge(FullSnapshot, FullState());

        Assert.AreEqual(5, next.DataVersion, "the merge is the snapshot's commit — the data version always advances");
    }

    [TestMethod]
    public void Merge_ProvidedSections_ReplaceThePreviousValues()
    {
        var next = WeatherSnapshotApplyPolicy.Merge(FullSnapshot, FullState());

        Assert.AreEqual(23.4, next.CurrentTempC);
        Assert.AreEqual(22.1, next.FeelsLikeC);
        Assert.AreEqual(65.0, next.Humidity);
        Assert.AreEqual(12.5, next.WindSpeedKmH);
        Assert.AreEqual(2, next.WeatherCode);
        Assert.AreEqual(27.0, next.HighTempC);
        Assert.AreEqual(18.0, next.LowTempC);
        Assert.AreEqual("Mon", next.DailyForecasts[0].DayName);
        Assert.AreEqual("12:00", next.HourlyForecasts[0].TimeLabel);
    }

    [TestMethod]
    public void Merge_ProvidedBothLists_BumpsForecastVersionPerList()
    {
        var next = WeatherSnapshotApplyPolicy.Merge(FullSnapshot, FullState());

        Assert.AreEqual(5, next.ForecastVersion, "each provided forecast list bumps the forecast version (3 -> 4 -> 5)");
    }

    [TestMethod]
    public void Merge_ProvidedList_ReplacesThePreviousReference()
    {
        var state = FullState();

        var next = WeatherSnapshotApplyPolicy.Merge(FullSnapshot, state);

        Assert.AreNotSame(state.DailyForecasts, next.DailyForecasts, "the provided list replaces the previous reference, it does not append");
    }

    [TestMethod]
    public void Merge_NullSections_KeepThePreviousValues()
    {
        var daily = new List<DailyForecastItem> { new("Sun", 15.0, 8.0, 61) };
        var hourly = new List<HourlyForecastItem> { new("09:00", 11.0, 61) };
        var state = new WeatherSnapshotState
        {
            DataVersion = 4,
            CurrentTempC = 10.0,
            FeelsLikeC = 9.0,
            Humidity = 80.0,
            WindSpeedKmH = 5.0,
            WeatherCode = 61,
            HighTempC = 15.0,
            LowTempC = 8.0,
            ForecastVersion = 3,
            DailyForecasts = daily,
            HourlyForecasts = hourly,
        };
        var snapshot = new WeatherSnapshot(
            CurrentTempC: null, FeelsLikeC: null, Humidity: null, WindSpeedKmH: null,
            WeatherCode: null, HighTempC: null, LowTempC: null,
            DailyForecasts: null, HourlyForecasts: null,
            ResolvedCityName: "City", Lat: 0, Lon: 0);

        var next = WeatherSnapshotApplyPolicy.Merge(snapshot, state);

        Assert.AreEqual(10.0, next.CurrentTempC);
        Assert.AreEqual(9.0, next.FeelsLikeC);
        Assert.AreEqual(80.0, next.Humidity);
        Assert.AreEqual(5.0, next.WindSpeedKmH);
        Assert.AreEqual(61, next.WeatherCode);
        Assert.AreEqual(15.0, next.HighTempC);
        Assert.AreEqual(8.0, next.LowTempC);
        Assert.AreSame(daily, next.DailyForecasts, "a null forecast section keeps the previous list reference");
        Assert.AreSame(hourly, next.HourlyForecasts);
        Assert.AreEqual(3, next.ForecastVersion, "no provided list means no forecast-version bump");
    }

    [TestMethod]
    public void Merge_ZeroScalar_ReplacesThePreviousValue()
    {
        var snapshot = new WeatherSnapshot(
            CurrentTempC: 0.0, FeelsLikeC: null, Humidity: null, WindSpeedKmH: null,
            WeatherCode: null, HighTempC: null, LowTempC: null,
            DailyForecasts: null, HourlyForecasts: null,
            ResolvedCityName: "City", Lat: 0, Lon: 0);

        var next = WeatherSnapshotApplyPolicy.Merge(snapshot, FullState());

        Assert.AreEqual(0.0, next.CurrentTempC,
            "a provided 0 must replace — 'no data' and 'keep previous' differ by null vs provided");
    }

    [TestMethod]
    public void Merge_ProvidedIsDay_ReplacesThePreviousValue()
    {
        var snapshot = new WeatherSnapshot(
            CurrentTempC: null, FeelsLikeC: null, Humidity: null, WindSpeedKmH: null,
            WeatherCode: null, HighTempC: null, LowTempC: null,
            DailyForecasts: null, HourlyForecasts: null,
            ResolvedCityName: "City", Lat: 0, Lon: 0, IsDay: false);

        var next = WeatherSnapshotApplyPolicy.Merge(snapshot, FullState());

        Assert.IsFalse(next.IsDay, "a provided night fact flips the day/night flag (the state's default is day)");
    }

    [TestMethod]
    public void Merge_AbsentIsDay_KeepsThePreviousValue()
    {
        var state = new WeatherSnapshotState
        {
            DataVersion = 4,
            WeatherCode = 61,
            IsDay = false,
        };
        var snapshot = new WeatherSnapshot(
            CurrentTempC: null, FeelsLikeC: null, Humidity: null, WindSpeedKmH: null,
            WeatherCode: null, HighTempC: null, LowTempC: null,
            DailyForecasts: null, HourlyForecasts: null,
            ResolvedCityName: "City", Lat: 0, Lon: 0);

        var next = WeatherSnapshotApplyPolicy.Merge(snapshot, state);

        Assert.IsFalse(next.IsDay,
            "an absent day/night fact null-keeps — a response that omitted is_day must not reset the flag");
    }

    [TestMethod]
    public void DefaultState_UnknownDayNight_ReadsAsDay()
    {
        var state = new WeatherSnapshotState();

        Assert.IsTrue(state.IsDay,
            "the pre-fetch placeholder scene reads as day — an unknown is_day must never render as night");
    }
}
