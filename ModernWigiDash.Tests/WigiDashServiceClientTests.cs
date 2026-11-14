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
    public void Dispose_WhileASensorReadIsInFlight_DoesNotWaitOnTheVendorCall()
    {
        // App teardown runs Dispose on the UI thread. A background refresh can
        // be inside a vendor call the binding allows 10 s to time out; Dispose
        // must detach and abort, not queue behind that call (the on-device
        // close spent ~16 s here).
        var channel = new FakeWcfChannel { BlockOnListOnce = true };
        var client = new WigiDashServiceClient(channel);
        try
        {
            Assert.IsNull(client.Sensors.GetSensorList(), "the first read schedules the background fetch");
            Assert.IsTrue(channel.ListEntered.Wait(TimeSpan.FromSeconds(5)),
                "the background refresh must reach the vendor call");

            var disposeThread = new Thread(client.Dispose) { IsBackground = true };
            disposeThread.Start();
            Assert.IsTrue(disposeThread.Join(TimeSpan.FromSeconds(10)),
                "teardown must not wait on the in-flight vendor call");
        }
        finally
        {
            channel.ListRelease.Set();
        }
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
        await WaitUntilAsync(() =>
        {
            _ = client.Sensors.GetSensorList();
            return opened >= 2;
        });
        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: > 0 });
        Assert.IsTrue(client.Sensors.IsReady);
    }

    [TestMethod]
    public void TryInitAidaProvider_FaultedChannel_DropsChannelSoNextAttemptCanReopen()
    {
        int opened = 0;
        var first = new FakeWcfChannel { ThrowOnAidaInit = true };
        var second = new FakeWcfChannel();
        using var client = new WigiDashServiceClient(
            () => Interlocked.Increment(ref opened) == 1 ? first : second,
            new FakeTimeProvider());

        Assert.IsFalse(client.TryInitAidaProvider(), "faulted AIDA provider init must return false");
        Assert.IsTrue(client.TryInitAidaProvider(), "after dropping the faulted channel, the next attempt must open a fresh channel and succeed");
        Assert.AreEqual(2, opened);
    }

    [TestMethod]
    public void TryWriteAidaMmap_FaultedChannel_DropsChannelSoNextInitCanReopen()
    {
        int opened = 0;
        var first = new FakeWcfChannel { ThrowOnAidaWrite = true };
        var second = new FakeWcfChannel();
        using var client = new WigiDashServiceClient(
            () => Interlocked.Increment(ref opened) == 1 ? first : second,
            new FakeTimeProvider());

        Assert.IsTrue(client.TryInitAidaProvider());
        Assert.IsFalse(client.TryWriteAidaMmap(0, new byte[4]), "faulted write must return false and drop the dead channel");
        Assert.IsTrue(client.TryInitAidaProvider(), "the next init must reopen a healthy channel instead of reusing the faulted one");
        Assert.AreEqual(2, opened);
    }

    [TestMethod]
    public async Task TryInitAidaProvider_WhileSensorListRefreshIsBlocked_DoesNotBlock()
    {
        var channel = new FakeWcfChannel { BlockOnListOnce = true };
        using var client = new WigiDashServiceClient(channel);
        try
        {
            Assert.IsNull(client.Sensors.GetSensorList());
            Assert.IsTrue(channel.ListEntered.Wait(TimeSpan.FromSeconds(5)));

            // Trigger AIDA init and check IsConnected while ListRelease stays unset.
            // Completing before ListRelease shows AIDA64 never waits on HWiNFO.
            var aidaTask = Task.Run(() => (Ok: client.TryInitAidaProvider(), Connected: client.IsConnected));
            var (ok, connected) = await aidaTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsFalse(channel.ListRelease.IsSet,
                "AIDA64 init and IsConnected must complete before releasing the blocked HWiNFO list read");
            Assert.IsTrue(ok);
            Assert.IsTrue(connected);
        }
        finally
        {
            channel.ListRelease.Set();
        }
    }

    [TestMethod]
    public void TryConnect_WhenChannelFactoryThrows_ReturnsFalseWithoutThrowing()
    {
        using var client = new WigiDashServiceClient(
            () => throw new InvalidOperationException("channel open failed"),
            new FakeTimeProvider());

        Assert.IsFalse(client.TryConnect());
        Assert.IsFalse(client.IsConnected);
        Assert.IsFalse(client.Sensors.IsReady);
    }

    [TestMethod]
    public void ReinitSensorProvider_WhenInitReturnsFalse_ReturnsFalseAndThrottlesRetry()
    {
        var channel = new FakeWcfChannel { InitResult = false };
        using var client = new WigiDashServiceClient(channel);

        Assert.IsFalse(client.ReinitSensorProvider(channel));
        Assert.AreEqual(1, channel.InitCalls);
        Assert.IsFalse(client.Sensors.IsReady);
        Assert.IsFalse(client.IsConnected);

        // Throttled within retry interval: second call skips WCF InitSensorProvider.
        Assert.IsFalse(client.ReinitSensorProvider(channel));
        Assert.AreEqual(1, channel.InitCalls);
    }

    [TestMethod]
    public async Task Sensors_FailedCallClearsCachedSensorListAndReconnectRestoresIt()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeWcfChannel
        {
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")],
        };
        using var client = new WigiDashServiceClient(() => channel, clock);

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: 1 });

        channel.ThrowOnList = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        _ = client.Sensors.GetSensorList();

        await WaitUntilAsync(() => !client.Sensors.IsReady && client.Sensors.GetSensorList() is null);
        Assert.IsNull(client.Sensors.GetSensorList(),
            "disconnecting/failing a refresh must clear stale cached sensors so widgets detect disconnect");

        channel.ThrowOnList = false;
        clock.Advance(TimeSpan.FromSeconds(6));
        _ = client.Sensors.GetSensorList();

        await WaitUntilAsync(() => client.Sensors.GetSensorList() is { Count: 1 });
        Assert.IsTrue(client.Sensors.IsReady);
    }

    [TestMethod]
    public async Task Sensors_ValueFailureWhenListAlsoRefreshes_ClearsCachedListAndReadings()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeWcfChannel
        {
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")],
        };
        using var client = new WigiDashServiceClient(() => channel, clock);

        // Prime both sensor list and cached reading (1, 2, 0).
        _ = client.Sensors.GetSensorValue(1, 2, 0);
        await WaitUntilAsync(() =>
            client.Sensors.GetSensorList() is { Count: 1 } &&
            client.Sensors.GetSensorValue(1, 2, 0) is not null);

        // Advance past SensorListTtl (2 s+) so the next refresh fetches a fresh list
        // (list != null) AND then fails reading sensor value (fetchFailed == true).
        channel.ThrowOnValue = true;
        clock.Advance(TimeSpan.FromSeconds(90));
        _ = client.Sensors.GetSensorList();
        _ = client.Sensors.GetSensorValue(1, 2, 0);

        await WaitUntilAsync(() => !client.Sensors.IsReady && client.Sensors.GetSensorList() is null);
        Assert.IsNull(client.Sensors.GetSensorList(),
            "fetchFailed must clear the cached list even when GetSensorList succeeded earlier in the same refresh");
        Assert.IsNull(client.Sensors.GetSensorValue(1, 2, 0),
            "fetchFailed must clear stale cached readings even when GetSensorList succeeded earlier in the same refresh");
    }

    [TestMethod]
    public async Task Sensors_KeyRequestedDuringInFlightRefresh_SurvivesPublishAndGetsFetched()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeWcfChannel { BlockOnValueOnce = true };
        using var client = new WigiDashServiceClient(() => channel, clock);

        Assert.IsNull(client.Sensors.GetSensorValue(1, 2, 0));
        Assert.IsTrue(channel.ValueEntered.Wait(TimeSpan.FromSeconds(5)));

        // Request a second key AFTER RequestedKeys() was snapshotted, while GetSensorValue is still in flight.
        Assert.IsNull(client.Sensors.GetSensorValue(3, 4, 0));

        channel.ValueRelease.Set();
        await WaitUntilAsync(() => client.Sensors.GetSensorValue(1, 2, 0) is not null);

        // Advance past the value TTL and poll ONLY (1, 2, 0); if (3, 4, 0) survived
        // PublishSnapshot in _requestedKeys, the next refresh fetches it too.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        _ = client.Sensors.GetSensorValue(1, 2, 0);

        await WaitUntilAsync(() => client.Sensors.GetSensorValue(3, 4, 0) is not null);
        Assert.AreEqual(42, client.Sensors.GetSensorValue(3, 4, 0)!.Value, 0.001);
    }

    [TestMethod]
    public async Task Dispose_WhileTryInitAidaProviderIsBlocked_AbortsChannelAndRejectsFollowOnInit()
    {
        int opened = 0;
        var channel = new FakeWcfChannel { BlockOnAidaInitOnce = true };
        var client = new WigiDashServiceClient(() =>
        {
            Interlocked.Increment(ref opened);
            return channel;
        }, new FakeTimeProvider());

        Task<bool> initTask = Task.Run(client.TryInitAidaProvider);
        try
        {
            Assert.IsTrue(channel.AidaInitEntered.Wait(TimeSpan.FromSeconds(5)));

            client.Dispose();
            Assert.AreEqual(1, channel.AbortCalls, "Dispose must abort the in-flight channel immediately");
            Assert.IsFalse(client.IsConnected);

            channel.AidaInitRelease.Set();
            Assert.IsFalse(await initTask.WaitAsync(TimeSpan.FromSeconds(5)),
                "an InitAidaProvider completing after Dispose must report false");

            Assert.IsFalse(client.TryInitAidaProvider(), "post-dispose init must be rejected without opening a channel");
            Assert.AreEqual(1, opened);
        }
        finally
        {
            channel.AidaInitRelease.Set();
            await initTask;
            client.Dispose();
        }
    }

    [TestMethod]
    public void Dispose_RacingTryInitAidaProvider_AbortsEveryOpenedChannelWithoutLeak()
    {
        for (int i = 0; i < 50; i++)
        {
            var channel = new FakeWcfChannel();
            int opened = 0;
            var client = new WigiDashServiceClient(() =>
            {
                Interlocked.Increment(ref opened);
                return channel;
            }, new FakeTimeProvider());

            using var startGate = new ManualResetEventSlim(false);
            Task<bool> initTask = Task.Run(() =>
            {
                startGate.Wait();
                return client.TryInitAidaProvider();
            });
            Task disposeTask = Task.Run(() =>
            {
                startGate.Wait();
                client.Dispose();
            });

            startGate.Set();
            Task.WaitAll(initTask, disposeTask);

            if (Volatile.Read(ref opened) > 0)
            {
                Assert.IsGreaterThanOrEqualTo(1, channel.AbortCalls,
                    $"iteration {i}: every opened channel must be aborted when Dispose races TryInitAidaProvider");
            }

            Assert.IsFalse(client.IsConnected);
            Assert.IsFalse(client.TryInitAidaProvider());
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition)
        => TestWait.WaitUntilAsync(condition, TimeSpan.FromSeconds(5));

    private sealed class FakeWcfChannel : IWigiDashWcf, System.ServiceModel.ICommunicationObject
    {
        private int _initCalls;
        private int _abortCalls;

        public bool InitCalled => Volatile.Read(ref _initCalls) > 0;
        public int InitCalls => Volatile.Read(ref _initCalls);
        public int AbortCalls => Volatile.Read(ref _abortCalls);

        public bool InitResult { get; set; } = true;

        public bool ThrowOnList { get; set; }

        public bool ThrowOnValue { get; set; }

        public bool ThrowOnInit { get; set; }

        public bool ThrowOnAidaInit { get; set; }

        public bool ThrowOnAidaWrite { get; set; }

        public bool IncludeNullSensor { get; set; }

        public bool BlockOnListOnce { get; set; }

        public bool BlockOnValueOnce { get; set; }

        public bool BlockOnAidaInitOnce { get; set; }

        public ManualResetEventSlim ListEntered { get; } = new(false);

        public ManualResetEventSlim ListRelease { get; } = new(false);

        public ManualResetEventSlim ValueEntered { get; } = new(false);

        public ManualResetEventSlim ValueRelease { get; } = new(false);

        public ManualResetEventSlim AidaInitEntered { get; } = new(false);

        public ManualResetEventSlim AidaInitRelease { get; } = new(false);

        private int _listBlocked;
        private int _valueBlocked;
        private int _aidaInitBlocked;

        public List<VendorSensorItem> Sensors { get; set; } = [];

        public bool InitSensorProvider(bool smbus, bool ec, bool presentmon)
        {
            if (ThrowOnInit) throw new InvalidOperationException("init dead");
            Interlocked.Increment(ref _initCalls);
            if (!InitResult) return false;
            Sensors = [new VendorSensorItem(Guid.NewGuid(), "CPU", 1, 2, 0, "P-core 0", "C")];
            return true;
        }

        public bool DeInitSensorProvider() => true;

        public int GetSensorInitStatus() => 1;

        public string? GetHwinfoSdkVersion() => "1.0";

        public List<VendorSensorItem> GetSensorList()
        {
            if (BlockOnListOnce && Interlocked.Exchange(ref _listBlocked, 1) == 0)
            {
                ListEntered.Set();
                ListRelease.Wait(TimeSpan.FromSeconds(30));
            }

            if (ThrowOnList) throw new InvalidOperationException("channel dead");
            return IncludeNullSensor ? [null!, .. Sensors] : Sensors;
        }

        public double GetSensorValue(int readingType, int sensorId1, int sensorId2, out bool isValid)
        {
            if (BlockOnValueOnce && Interlocked.Exchange(ref _valueBlocked, 1) == 0)
            {
                ValueEntered.Set();
                ValueRelease.Wait(TimeSpan.FromSeconds(30));
            }

            if (ThrowOnValue) throw new InvalidOperationException("value dead");
            isValid = true;
            return 42;
        }

        public bool InitAidaProvider()
        {
            if (BlockOnAidaInitOnce && Interlocked.Exchange(ref _aidaInitBlocked, 1) == 0)
            {
                AidaInitEntered.Set();
                AidaInitRelease.Wait(TimeSpan.FromSeconds(30));
            }

            if (ThrowOnAidaInit) throw new InvalidOperationException("aida init dead");
            return true;
        }

        public bool WriteAidaMmap(int offset, byte[] buffer)
        {
            if (ThrowOnAidaWrite) throw new InvalidOperationException("aida write dead");
            return true;
        }

        private EventHandler? _lifecycleHandlers;

        System.ServiceModel.CommunicationState System.ServiceModel.ICommunicationObject.State
            => AbortCalls > 0 ? System.ServiceModel.CommunicationState.Closed : System.ServiceModel.CommunicationState.Opened;

        event EventHandler? System.ServiceModel.ICommunicationObject.Closed
        {
            add => _lifecycleHandlers += value;
            remove => _lifecycleHandlers -= value;
        }
        event EventHandler? System.ServiceModel.ICommunicationObject.Closing
        {
            add => _lifecycleHandlers += value;
            remove => _lifecycleHandlers -= value;
        }
        event EventHandler? System.ServiceModel.ICommunicationObject.Faulted
        {
            add => _lifecycleHandlers += value;
            remove => _lifecycleHandlers -= value;
        }
        event EventHandler? System.ServiceModel.ICommunicationObject.Opened
        {
            add => _lifecycleHandlers += value;
            remove => _lifecycleHandlers -= value;
        }
        event EventHandler? System.ServiceModel.ICommunicationObject.Opening
        {
            add => _lifecycleHandlers += value;
            remove => _lifecycleHandlers -= value;
        }

        void System.ServiceModel.ICommunicationObject.Abort() => Interlocked.Increment(ref _abortCalls);
        void System.ServiceModel.ICommunicationObject.Open() => throw new NotSupportedException();
        void System.ServiceModel.ICommunicationObject.Open(TimeSpan timeout) => throw new NotSupportedException();
        IAsyncResult System.ServiceModel.ICommunicationObject.BeginOpen(AsyncCallback? callback, object? state) => throw new NotSupportedException();
        IAsyncResult System.ServiceModel.ICommunicationObject.BeginOpen(TimeSpan timeout, AsyncCallback? callback, object? state) => throw new NotSupportedException();
        void System.ServiceModel.ICommunicationObject.EndOpen(IAsyncResult result) => throw new NotSupportedException();
        void System.ServiceModel.ICommunicationObject.Close() => throw new NotSupportedException();
        void System.ServiceModel.ICommunicationObject.Close(TimeSpan timeout) => throw new NotSupportedException();
        IAsyncResult System.ServiceModel.ICommunicationObject.BeginClose(AsyncCallback? callback, object? state) => throw new NotSupportedException();
        IAsyncResult System.ServiceModel.ICommunicationObject.BeginClose(TimeSpan timeout, AsyncCallback? callback, object? state) => throw new NotSupportedException();
        void System.ServiceModel.ICommunicationObject.EndClose(IAsyncResult result) => throw new NotSupportedException();
    }
}
