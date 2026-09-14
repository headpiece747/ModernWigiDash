using ModernWigiDash.Hardware.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class WigiDashServiceClientTests
{
    [TestMethod]
    public void TryConnect_VerifiesConnectionStateConsistency()
    {
        // The vendor service may or may not be running on this machine; the
        // invariant is that IsConnected reflects the TryConnect verdict.
        using var client = new WigiDashServiceClient();
        var connected = client.TryConnect();
        Assert.AreEqual(connected, client.IsConnected);
    }

    [TestMethod]
    public void Sensors_BeforeARead_IsNotReadyAndReadsNoValue()
    {
        // IsReady is a pure probe (no I/O): a fresh client is not ready until a
        // background refresh connects and initializes the provider. Reads are
        // served from the background cache, so a fresh client returns no data.
        using var client = new WigiDashServiceClient();
        Assert.IsFalse(client.Sensors.IsReady, "a fresh client is not ready until it connects");
        Assert.IsNull(client.Sensors.GetSensorValue(0, 0, 0), "no reading is cached yet");
        Assert.IsNull(client.Sensors.GetSensorList(), "no list is cached yet");
    }

    [TestMethod]
    public async Task Sensors_WhenConnected_ListDeserializesWithTheVendorContract()
    {
        // Regression pin for the 2026-09-13 on-device bug: the mirror's data
        // contract must carry the vendor's SensorItem name/namespace, or WCF
        // silently deserializes the whole response to an empty list and the
        // HWiNFO widget shows its placeholder forever. Tolerant on machines
        // without the vendor Manager (the ADR-0017 absent-service path).
        using var client = new WigiDashServiceClient();
        if (!client.TryConnect())
            return;

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });

        var sensors = client.Sensors.GetSensorList();
        Assert.IsNotNull(sensors,
            "a connected vendor service must deserialize its sensor list, not drop it as an empty sequence");
        Assert.IsTrue(sensors.Count > 0,
            "the vendor service reports HWiNFO's sensors; an empty list means the data-contract mirror is wrong");
    }

    [TestMethod]
    public void Dispose_WhenNeverConnected_DoesNotThrow()
    {
        var client = new WigiDashServiceClient();
        client.Dispose();
        // Second dispose is a no-op; IsConnected must be false after dispose.
        Assert.IsFalse(client.IsConnected);
        client.Dispose();
    }

    [TestMethod]
    public void TryConnect_AfterDispose_ReturnsFalse()
    {
        var client = new WigiDashServiceClient();
        client.Dispose();
        Assert.IsFalse(client.TryConnect());
    }

    [TestMethod]
    public void VendorSensorItem_RecordShape_MatchesVendorContract()
    {
        var item = new VendorSensorItem(Guid.NewGuid(), "CPU", 0, 1, 2, "Temperature", "°C");
        Assert.AreEqual("CPU", item.Name);
        Assert.AreEqual(0, item.ReadingType);
        Assert.AreEqual(1, item.SensorId1);
        Assert.AreEqual(2, item.SensorId2);
        Assert.AreEqual("Temperature", item.Type);
        Assert.AreEqual("°C", item.Unit);
    }

    [TestMethod]
    public void VendorSensorReading_RecordShape_CarriesValueAndValidity()
    {
        var reading = new VendorSensorReading(42.5, true);
        Assert.AreEqual(42.5, reading.Value);
        Assert.IsTrue(reading.IsValid);

        var invalid = new VendorSensorReading(0, false);
        Assert.IsFalse(invalid.IsValid);
    }

    [TestMethod]
    public async Task Sensors_EmptyListFromATornDownProvider_ReinitializesAndRecovers()
    {
        // The vendor's HWiNFO provider is a shared session; when another
        // consumer (the vendor Manager) deinits it, GetSensorList returns an
        // empty list, not an error. The background refresh must re-initialize
        // the provider and publish the recovered list.
        var channel = new FakeWcfChannel();
        using var client = new WigiDashServiceClient(channel);

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });

        Assert.IsTrue(channel.InitCalled, "an empty sensor list must trigger a provider re-initialization");
        Assert.IsNotNull(client.Sensors.GetSensorList());
        Assert.AreEqual(1, client.Sensors.GetSensorList()!.Count);
    }

    [TestMethod]
    public async Task Sensors_NonEmptyList_DoesNotReinitialize()
    {
        var channel = new FakeWcfChannel
        {
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")],
        };
        using var client = new WigiDashServiceClient(channel);

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });

        Assert.IsFalse(channel.InitCalled);
        Assert.AreEqual(1, client.Sensors.GetSensorList()!.Count);
    }

    [TestMethod]
    public async Task Sensors_FailedCallDropsTheChannelSoTheClientCanReconnect()
    {
        // A faulted/dead channel must degrade to no data AND be dropped, so the
        // next refresh reopens it (the app self-heals when the vendor service
        // restarts; the former client kept the dead channel forever).
        var channel = new FakeWcfChannel { ThrowOnList = true };
        using var client = new WigiDashServiceClient(channel);

        Assert.IsNull(client.Sensors.GetSensorList(), "a failed call must degrade to no data, not throw");

        await WaitUntilAsync(() => !client.Sensors.IsReady);

        Assert.IsFalse(client.Sensors.IsReady, "the dead channel must be dropped for the next attempt to reopen");
    }

    [TestMethod]
    public async Task Sensors_FailedValueReadDropsTheChannelSoTheClientCanReconnect()
    {
        var channel = new FakeWcfChannel { ThrowOnValue = true };
        using var client = new WigiDashServiceClient(channel);

        Assert.IsNull(client.Sensors.GetSensorValue(1, 2, 0), "a failed value read must degrade to no data");

        await WaitUntilAsync(() => !client.Sensors.IsReady);
        Assert.IsFalse(client.Sensors.IsReady, "the dead channel must be dropped");
    }

    [TestMethod]
    public async Task Sensors_InitThrows_DropsTheChannelSoTheNextAttemptCanReopen()
    {
        int opened = 0;
        var channel = new FakeWcfChannel { ThrowOnInit = true };
        using var client = new WigiDashServiceClient(() => { opened++; return channel; }, new FakeTimeProvider());

        Assert.IsNull(client.Sensors.GetSensorList(), "a throwing init must degrade to no data");

        await WaitUntilAsync(() => opened >= 1);
        Assert.IsFalse(client.Sensors.IsReady, "the faulted channel must be dropped, not retried in place forever");
    }

    [TestMethod]
    public async Task Sensors_NilListElement_IsSkippedNotThrown()
    {
        // A nil element (<SensorItem i:nil="true"/>) deserializes to null; the
        // old projection threw an NRE that reached the render tick uncaught and
        // killed the process.
        var channel = new FakeWcfChannel
        {
            IncludeNullSensor = true,
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")],
        };
        using var client = new WigiDashServiceClient(channel);

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });

        Assert.AreEqual(1, client.Sensors.GetSensorList()!.Count, "a nil list element must be skipped, not projected");
    }

    [TestMethod]
    public async Task Sensors_AfterAFailedCall_ReopensTheChannelOnceTheRetryWindowElapses()
    {
        // The full self-heal path through the injected channel factory + clock:
        // the background refresh reads, the channel dies, drops, and after the
        // retry window reopens and recovers (no app relaunch).
        var clock = new FakeTimeProvider();
        int opened = 0;
        var channel = new FakeWcfChannel
        {
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")],
        };
        using var client = new WigiDashServiceClient(() => { opened++; return channel; }, clock);

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });
        Assert.AreEqual(1, opened, "the first refresh opens the channel");
        Assert.IsTrue(client.Sensors.IsReady);

        // The channel dies: the next (stale) read schedules a refresh, which
        // fails and drops the channel.
        channel.ThrowOnList = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        _ = client.Sensors.GetSensorList();
        await WaitUntilAsync(() => !client.Sensors.IsReady);

        // The reopen is rate-limited: no attempt inside the retry window.
        channel.ThrowOnList = false;
        clock.Advance(TimeSpan.FromMilliseconds(200));
        _ = client.Sensors.GetSensorList();
        Assert.AreEqual(1, opened, "no reopen inside the retry window");

        // Past the windows: the next refresh reopens and recovers.
        clock.Advance(TimeSpan.FromSeconds(6));
        _ = client.Sensors.GetSensorList();
        await WaitUntilAsync(() => opened >= 2);
        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });
        Assert.IsTrue(client.Sensors.IsReady);
    }

    private static Task WaitUntilAsync(Func<bool> condition)
        => TestWait.WaitUntilAsync(condition, TimeSpan.FromSeconds(5));

    private sealed class FakeWcfChannel : IWigiDashWcf
    {
        public bool InitCalled { get; private set; }

        public bool ThrowOnList { get; set; }

        public bool ThrowOnValue { get; set; }

        public bool ThrowOnInit { get; set; }

        public bool IncludeNullSensor { get; set; }

        public List<VendorSensorItem> Sensors { get; set; } = [];

        public bool InitSensorProvider(bool smbus, bool ec, bool presentmon)
        {
            if (ThrowOnInit) throw new InvalidOperationException("init dead");
            InitCalled = true;
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")];
            return true;
        }

        public bool DeInitSensorProvider() => true;

        public int GetSensorInitStatus() => 1;

        public string? GetHwinfoSdkVersion() => "1.0";

        public List<VendorSensorItem> GetSensorList()
        {
            if (ThrowOnList) throw new InvalidOperationException("channel dead");
            return IncludeNullSensor ? [null!, .. Sensors] : Sensors;
        }

        public double GetSensorValue(int readingType, int sensorId1, int sensorId2, out bool isValid)
        {
            if (ThrowOnValue) throw new InvalidOperationException("value dead");
            isValid = true;
            return 42;
        }
    }
}
