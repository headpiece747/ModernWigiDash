using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Unit tests for <see cref="WeatherRuntimeEngine"/> (Candidate 4): asserts the deep
/// weather runtime engine encapsulates geocode ranking, Open-Meteo REST fetching,
/// atomic display state, disk caching, and writeback lifecycle without requiring a WPF widget.
/// </summary>
[TestClass]
public class WeatherRuntimeEngineTests
{
    private const string SampleGeocode = """
    {
      "results": [ { "name": "New York", "latitude": 40.7128, "longitude": -74.006, "country": "US" } ]
    }
    """;

    private static string TempCacheDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "wmd_weather_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public void Start_Stop_Dispose_IsIdempotent()
    {
        string cacheDir = TempCacheDir();
        try
        {
            using var engine = new WeatherRuntimeEngine(
                cacheDir: cacheDir,
                instanceIdProvider: () => "test-instance",
                locationProvider: () => new WeatherLocation("Fixed Location", "Berlin, Germany", null, null, null),
                isStaticSnapshotProvider: () => false);

            engine.Start();
            engine.Start();
            engine.Dispose();
            engine.Dispose();

            Assert.IsNotNull(engine);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_WithStubHttp_UpdatesSnapshotStateAndResolvedIdentity()
    {
        string cacheDir = TempCacheDir();
        try
        {
            var stub = new StubHttpHandler(request =>
            {
                string url = request.RequestUri?.AbsoluteUri ?? "";
                if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleGeocode);
                return StubHttpHandler.Ok(WeatherTestData.SampleForecastLegacy);
            });

            using var engine = new WeatherRuntimeEngine(
                cacheDir: cacheDir,
                instanceIdProvider: () => "test-ny",
                locationProvider: () => new WeatherLocation("Fixed Location", "New York", null, null, null),
                isStaticSnapshotProvider: () => false)
            {
                TestHttpClient = new HttpClient(stub)
            };

            var outcome = await engine.FetchLiveWeatherAsync(force: true);

            Assert.AreEqual(WeatherFetchFlowOutcome.Applied, outcome);
            Assert.AreEqual("New York, US", engine.ResolvedCityName);
            Assert.AreEqual(1, engine.SnapshotState.DataVersion);
            Assert.IsTrue(engine.SnapshotState.DailyForecasts.Count > 0);
            Assert.IsTrue(engine.SnapshotState.HourlyForecasts.Count > 0);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public void LocationWriteback_QueuesAndFlushesViaOnWritebackCallback()
    {
        string cacheDir = TempCacheDir();
        try
        {
            string? writtenBack = null;
            using var engine = new WeatherRuntimeEngine(
                cacheDir: cacheDir,
                instanceIdProvider: () => "test-writeback",
                locationProvider: () => new WeatherLocation("Fixed Location", "Berlin", null, null, null),
                isStaticSnapshotProvider: () => false,
                onWriteback: label => writtenBack = label);

            // Queue a writeback through the internal client seam
            engine.DisplayState.Client.QueueLabelWriteback(() => true, "Berlin, State of Berlin, Germany");

            // Flush
            engine.ApplyPendingLocationWriteback();

            Assert.AreEqual("Berlin, State of Berlin, Germany", writtenBack);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public void Invalidate_Location_ClearsIdentityAndForcesFetchCadence()
    {
        string cacheDir = TempCacheDir();
        try
        {
            using var engine = new WeatherRuntimeEngine(
                cacheDir: cacheDir,
                instanceIdProvider: () => "test-inval",
                locationProvider: () => new WeatherLocation("Fixed Location", "Munich", null, null, null),
                isStaticSnapshotProvider: () => false);

            engine.Invalidate(WeatherInvalidationKind.Location);

            Assert.AreEqual("", engine.ResolvedCityName);
            Assert.AreEqual(0, engine.Candidates.Count);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }
}
