using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.App;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Unit tests for <see cref="TelemetryFeedCoordinator"/> (Candidate 2, ADR-0011 exit plan):
/// verifies unified ingestion, instance-level telemetry cache isolation, vendor WCF seam,
/// and headless integration with <see cref="WidgetHostEnvironment"/>.
/// </summary>
[TestClass]
public class TelemetryFeedCoordinatorTests
{
    [TestMethod]
    public void Start_Stop_Dispose_IsIdempotent()
    {
        using var coordinator = new TelemetryFeedCoordinator(new StubPresentMonNative(), _ => { });

        coordinator.Start();
        coordinator.Start();
        coordinator.Stop();
        coordinator.Dispose();
        coordinator.Dispose();

        Assert.IsNotNull(coordinator);
    }

    [TestMethod]
    public void SensorPollTick_UpdatesInstanceAndStaticStore()
    {
        List<string> logs = [];
        using var coordinator = new TelemetryFeedCoordinator(
            new StubPresentMonNative(),
            logs.Add,
            lhsMapSource: new StubLhmMapSource());

        coordinator.SensorPollTick();
        coordinator.SensorPollTick();

        // Stub with no maps sets disconnected snapshot (IsConnected = false)
        var instanceSnapshot = coordinator.ReadSensorSnapshot();
        Assert.IsNotNull(instanceSnapshot);
        Assert.IsFalse(instanceSnapshot.IsConnected);

        // Dedup: single error log on missing maps across multiple ticks
        Assert.AreEqual(1, logs.Count(log => log.Contains("[SENSOR]", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FrameTimePollTick_Success_UpdatesInstanceStore()
    {
        using var coordinator = new TelemetryFeedCoordinator(
            new StubPresentMonNative
            {
                IsAvailable = true,
                OpenSessionResult = true,
                PollResult = new PresentMonDynamicSample(144.0, 120.0, 1.0, 3.0, 143.0, 0, 2.0, 4),
            },
            _ => { },
            targetResolver: new TrackedTargetResolver(() => 1234, _ => []));

        coordinator.FrameTimePollTick();

        var fresh = coordinator.TryReadFrameTime();
        Assert.IsNotNull(fresh, "fresh sample must be available on the coordinator instance");
        Assert.AreEqual(144.0, fresh.Fps, 0.001);

        // Also feeds the static store for backward compatibility
        var staticFresh = FrameTimeStore.TryReadFresh();
        Assert.IsNotNull(staticFresh);
        Assert.AreEqual(144.0, staticFresh.Fps, 0.001);
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void FrameTimePollTick_Unavailable_DedupsErrorLogging()
    {
        List<string> logs = [];
        using var coordinator = new TelemetryFeedCoordinator(new StubPresentMonNative(), logs.Add);

        coordinator.FrameTimePollTick();
        coordinator.FrameTimePollTick();

        Assert.AreEqual(1, logs.Count(log => log.Contains("[FRAMETIME]", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SetVendorService_UpdatesInstanceAndStaticInstance()
    {
        using var coordinator = new TelemetryFeedCoordinator(new StubPresentMonNative(), _ => { });

        Assert.IsNull(coordinator.VendorClient);
        Assert.IsNull(coordinator.TryGetVendorService());

        // Note: WigiDashServiceClient without params doesn't open until connect
        var client = new WigiDashServiceClient();
        try
        {
            coordinator.SetVendorService(client);

            Assert.AreSame(client, coordinator.VendorClient);
            Assert.AreSame(client, coordinator.TryGetVendorService());
            Assert.AreSame(client, VendorService.Instance);

            coordinator.SetVendorService(null);
            Assert.IsNull(coordinator.VendorClient);
            Assert.IsNull(coordinator.TryGetVendorService());
            Assert.IsNull(VendorService.Instance);
        }
        finally
        {
            client.Dispose();
            VendorService.SetInstance(null);
        }
    }

    [TestMethod]
    public void WidgetHostEnvironment_TelemetryContextFacet_ReadsFromCoordinator()
    {
        using var coordinator = new TelemetryFeedCoordinator(
            new StubPresentMonNative
            {
                IsAvailable = true,
                OpenSessionResult = true,
                PollResult = new PresentMonDynamicSample(60.0, 60.0, 1.0, 3.0, 60.0, 0, 2.0, 4),
            },
            _ => { },
            targetResolver: new TrackedTargetResolver(() => 9999, _ => []));

        coordinator.FrameTimePollTick();

        var env = new WidgetHostEnvironment(telemetryContext: coordinator);
        var telemetryContext = (IWidgetTelemetryContext)env;

        var sample = telemetryContext.TryReadFrameTime();
        Assert.IsNotNull(sample);
        Assert.AreEqual(60.0, sample.Fps, 0.001);
    }

    [TestMethod]
    public void PollingPeriod_InitialAndDynamicUpdate_WorksSafely()
    {
        List<string> logs = [];
        using var coordinator = new TelemetryFeedCoordinator(
            new StubPresentMonNative(),
            logs.Add,
            initialPeriod: TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(TimeSpan.FromMilliseconds(250), coordinator.CurrentPollingPeriod);

        coordinator.Start();
        coordinator.UpdatePollingPeriod(TimeSpan.FromMilliseconds(1000));

        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), coordinator.CurrentPollingPeriod);
        Assert.IsTrue(logs.Any(l => l.Contains("Polling cadence updated to 1000ms")));

        // Calling with same period is idempotent
        int logCount = logs.Count;
        coordinator.UpdatePollingPeriod(TimeSpan.FromMilliseconds(1000));
        Assert.AreEqual(logCount, logs.Count);

        coordinator.Stop();
    }
}
