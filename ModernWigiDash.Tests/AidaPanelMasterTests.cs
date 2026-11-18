using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Tests;

/// <summary>
/// The AIDA64 panel master's vendor protocol: slot registration (the header plus
/// one 5x4 widget record), the heartbeat/slave/marker poll, the published copy,
/// and the frame ack. Driven through the read/write seams, so no map and no
/// service are involved. The protocol is the one the vendor Manager runs
/// (verified against the live service 2026-09-14: registering + acking makes
/// AIDA64 publish ~1 frame a second).
/// </summary>
[TestClass]
public sealed class AidaPanelMasterTests
{
    [TestMethod]
    public void EnsureRegistered_WritesTheHeaderAndTheWidgetRecord()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        Assert.IsTrue(master.EnsureRegistered());
        Assert.IsTrue(master.IsRegistered);

        var block = WriteAt(writer, AidaPanelMaster.MapBlockOffset);
        Assert.IsNotNull(block);
        Assert.AreEqual(AidaPanelMaster.MapBlockSize, block.Length);
        Assert.AreEqual(AidaPanelMaster.MmapMagicTag, BitConverter.ToUInt32(block, 0), "magic tag");
        Assert.AreEqual(AidaPanelMaster.WidgetVersion, BitConverter.ToInt32(block, 4));
        Assert.AreEqual(1, BitConverter.ToInt32(block, 8), "NumWidgets");
        Assert.AreEqual(1, BitConverter.ToInt32(block, 12), "ConfigUpdated");
        Assert.AreEqual(1016, BitConverter.ToInt32(block, 32), "registered widget width");
        Assert.AreEqual(592, BitConverter.ToInt32(block, 36), "registered widget height");
        Assert.AreEqual(5, BitConverter.ToInt32(block, 40), "width tiles");
        Assert.AreEqual(4, BitConverter.ToInt32(block, 44), "height tiles");
        Assert.AreEqual(135174, BitConverter.ToInt32(block, 80), "RGB565 format");
        Assert.AreEqual(AidaPanelMaster.BitmapSize, BitConverter.ToInt32(block, 84), "bitmap size");
        Assert.AreEqual(AidaPanelMaster.BitmapOffset, BitConverter.ToInt32(block, 88), "bitmap offset");
    }

    [TestMethod]
    public void EnsureRegistered_InitFails_ReturnsFalseAndWritesNothing()
    {
        var writer = new FakeAidaMmapWriter { InitResult = false };
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        Assert.IsFalse(master.EnsureRegistered());
        Assert.IsFalse(master.IsRegistered);
        Assert.AreEqual(0, writer.Writes.Count);
    }

    [TestMethod]
    public void EnsureRegistered_IsIdempotent()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        Assert.IsTrue(master.EnsureRegistered());
        Assert.IsTrue(master.EnsureRegistered());

        Assert.AreEqual(1, writer.InitCalls);
        Assert.AreEqual(1, writer.Writes.Count(w => w.Offset == AidaPanelMaster.MapBlockOffset));
    }

    [TestMethod]
    public void PollOnce_NewFrameMarker_PublishesACopyAndAcks()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();

        var published = master.TryGetPublished();
        Assert.IsNotNull(published, "a set marker is a new frame");
        Assert.AreEqual(64, published.Width);
        Assert.AreEqual(32, published.Height);
        Assert.AreEqual(64 * 32 * 2, published.Length);
        Assert.AreEqual(1, published.Generation);
        Assert.AreEqual(0x1F, published.Pixels[0], "the published copy starts at the pixels, not the BMP header");
        Assert.AreEqual(0x08, published.Pixels[1]);

        var ack = WriteAt(writer, AidaPanelMaster.BitmapOffset);
        Assert.IsNotNull(ack, "the frame must be acknowledged so AIDA64 writes the next one");
        CollectionAssert.AreEqual(new byte[4], ack, "the ack writes zero to the marker");
    }

    [TestMethod]
    public void PollOnce_ZeroMarker_DoesNotPublish()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32, markerSet: false)), writer);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();

        Assert.IsNull(master.TryGetPublished());
        Assert.IsNull(WriteAt(writer, AidaPanelMaster.BitmapOffset), "a consumed slot is not acked again");
    }

    [TestMethod]
    public void PollOnce_BeforeTheSlaveMoves_DoesNotConsume()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32, slaveCounter: 0)), writer);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();

        Assert.IsNull(master.TryGetPublished(), "a slot AIDA64 has never written is not consumed");
        Assert.IsNull(WriteAt(writer, AidaPanelMaster.BitmapOffset));
    }

    [TestMethod]
    public void PollOnce_BeforeRegistration_DoesNothing()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        master.PollOnce();

        Assert.IsNull(master.TryGetPublished());
        Assert.AreEqual(0, writer.Writes.Count, "no heartbeat and no ack before the slot is registered");
    }

    [TestMethod]
    public void Start_WhileTheVendorIoIsBlocked_ReturnsWithoutWaiting()
    {
        // The render tick calls Start() every frame. The poll loop sits inside
        // vendor I/O (a WCF call bounded by the binding's 10 s send timeout), so
        // the tick must not wait on any gate that loop holds. This is the
        // 2026-11-11 "adding the AIDA64 widget freezes the app" report.
        var writer = new BlockingAidaMmapWriter();
        var master = new AidaPanelMaster(
            new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer, log: static _ => { });
        try
        {
            master.Start();
            Assert.IsTrue(writer.WaitUntilEntered(TimeSpan.FromSeconds(5)),
                "the poll loop must reach the vendor provider init");

            var completed = false;
            var thread = new Thread(() =>
            {
                master.Start();
                completed = true;
            })
            {
                IsBackground = true
            };
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)) && completed,
                "a render-tick Start must not block behind vendor I/O");
        }
        finally
        {
            writer.Release();
            master.Dispose();
            writer.Dispose();
        }
    }

    [TestMethod]
    public void EnsureRegistered_DisposedDuringTheProviderInit_WritesNoSlot()
    {
        // A vendor call that returns after teardown must not register slot 0
        // on a master the app already disposed.
        var writer = new BlockingAidaMmapWriter();
        var master = new AidaPanelMaster(
            new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer, log: static _ => { });
        try
        {
            Task<bool> probe = Task.Run(master.EnsureRegistered);
            Assert.IsTrue(writer.WaitUntilEntered(TimeSpan.FromSeconds(5)));

            master.Dispose();
            writer.Release();

            Assert.IsTrue(probe.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(probe.Result, "a post-teardown registration is refused");
            Assert.AreEqual(0, writer.Writes.Count, "no slot write may land after teardown");
        }
        finally
        {
            writer.Release();
            writer.Dispose();
        }
    }

    [TestMethod]
    public void PollOnce_RepeatedWriteFailures_ClearsRegistrationSoServiceRestartRecovers()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = new AidaPanelMaster(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer, log: static _ => { });
        Assert.IsTrue(master.EnsureRegistered());

        writer.WriteResult = false;
        master.PollOnce();
        Assert.IsTrue(master.IsRegistered, "a single transient write failure must not drop registration");

        master.PollOnce();
        Assert.IsTrue(master.IsRegistered, "two write failures must keep registration before threshold 3");

        master.PollOnce();
        Assert.IsFalse(master.IsRegistered,
            "three consecutive write failures mean the vendor service restarted or lost the map; registration must reset so EnsureRegistered runs again");
        Assert.IsNull(master.TryGetPublished(),
            "heartbeat write failure on tick 3 must abort the tick without publishing or acking");

        writer.WriteResult = true;
        master.PollOnce();
        Assert.IsFalse(master.IsRegistered, "PollOnce stays idle until EnsureRegistered re-registers slot 0");
        Assert.IsTrue(master.EnsureRegistered());
        Assert.IsTrue(master.IsRegistered);
    }

    [TestMethod]
    public void TryConsumeAvailableFrame_TornFrameOnce_RetriesBeforeClearingMarker()
    {
        byte[] map = AidaTestMap.BuildValid(64, 32);
        map[AidaMmapReader.MapHeaderSize] = (byte)'X';
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(map), writer);
        Assert.IsTrue(master.EnsureRegistered());

        // First torn read: marker ack must be deferred so the next tick can retry.
        master.PollOnce();
        Assert.IsNull(master.TryGetPublished());
        Assert.IsNull(WriteAt(writer, AidaPanelMaster.BitmapOffset),
            "first torn frame read must not clear the marker");

        // Second consecutive failure clears the marker to keep AIDA64 advancing.
        master.PollOnce();
        Assert.IsNotNull(WriteAt(writer, AidaPanelMaster.BitmapOffset),
            "second consecutive torn read must clear the marker");
    }

    [TestMethod]
    public void PollOnce_SlaveStagnatesPastTimeout_ClearsPublishedFrame()
    {
        var clock = new FakeTimeProvider();
        var writer = new FakeAidaMmapWriter();
        using var master = new AidaPanelMaster(
            new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32, slaveCounter: 1)),
            writer,
            log: static _ => { },
            timeProvider: clock);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();
        Assert.IsNotNull(master.TryGetPublished());

        clock.Advance(TimeSpan.FromSeconds(5));
        master.PollOnce();

        Assert.IsNull(master.TryGetPublished(), "stale frame must clear after 5 s of slave inactivity");
        Assert.AreEqual("Waiting for AIDA64 frames", master.LastError);
    }

    [TestMethod]
    public void PollOnce_ExternalMapResetWhileIdle_ReRegistersSlot()
    {
        byte[] map = AidaTestMap.BuildValid(64, 32, slaveCounter: 0);
        var source = new TrackingResetSource(map);
        var writer = new FakeAidaMmapWriter();
        using var master = Create(source, writer);
        Assert.IsTrue(master.EnsureRegistered());
        int resetsAfterRegister = source.ResetCalls;

        // 9 ticks stay below the 10-tick header check cadence.
        for (int i = 0; i < 9; i++)
        {
            master.PollOnce();
        }
        Assert.IsTrue(master.IsRegistered);

        // External wipe of MmapMagicTag while AIDA64 is still idle triggers reset on tick 10.
        BitConverter.GetBytes(0u).CopyTo(map, AidaMmapReader.MagicTagOffset);
        master.PollOnce();

        Assert.IsFalse(master.IsRegistered, "external magic wipe must clear registration even while idle");
        Assert.IsGreaterThan(resetsAfterRegister, source.ResetCalls);
    }

    [TestMethod]
    public void PollOnce_WriteFailuresClearRegistration_ClearsStalePublishedFrame()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32, slaveCounter: 1)), writer);
        Assert.IsTrue(master.EnsureRegistered());
        master.PollOnce();
        Assert.IsNotNull(master.TryGetPublished());

        writer.WriteResult = false;
        master.PollOnce();
        master.PollOnce();
        master.PollOnce();

        Assert.IsFalse(master.IsRegistered);
        Assert.IsNull(master.TryGetPublished(), "losing registration must clear any stale published frame");
    }

    [TestMethod]
    public void UpdatePollingPeriod_WhileVendorIoBlocked_ReturnsWithoutWaiting()
    {
        var writer = new BlockingAidaMmapWriter();
        var master = new AidaPanelMaster(
            new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer, log: static _ => { });
        try
        {
            master.Start();
            Assert.IsTrue(writer.WaitUntilEntered(TimeSpan.FromSeconds(5)));

            bool completed = false;
            var thread = new Thread(() =>
            {
                master.UpdatePollingPeriod(TimeSpan.FromMilliseconds(250));
                completed = true;
            })
            {
                IsBackground = true,
            };
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)) && completed,
                "UpdatePollingPeriod must update in place without blocking behind vendor I/O");
        }
        finally
        {
            writer.Release();
            master.Dispose();
            writer.Dispose();
        }
    }

    [TestMethod]
    public void PollOnce_RepeatedMmapReadFailures_PreservesReadErrorWithoutDroppingRegistration()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(), writer);
        Assert.IsTrue(master.EnsureRegistered());

        // Header checks fail after 10 ticks when map is absent; WCF writes stay healthy.
        for (int i = 0; i < 15; i++)
        {
            master.PollOnce();
        }

        Assert.IsTrue(master.IsRegistered, "healthy WCF writes must not unregister slot 0 on MMF absence");
        Assert.AreEqual("AIDA64 map unavailable", master.LastError);
    }

    private sealed class TrackingResetSource(byte[] bytes) : IAidaMmapSource
    {
        private readonly FakeAidaMmapSource _inner = new(bytes);
        public int ResetCalls { get; private set; }

        public bool TryRead(int offset, int length, byte[] destination, out string? error)
            => _inner.TryRead(offset, length, destination, out error);

        public void Reset() => ResetCalls++;

        public void Dispose() => _inner.Dispose();
    }

    private static AidaPanelMaster Create(IAidaMmapSource source, FakeAidaMmapWriter writer)
        => new(source, writer, log: static _ => { });

    /// <summary>A writer that parks the first provider-init call until released,
    /// standing in for a slow or wedged vendor WCF call.</summary>
    private sealed class BlockingAidaMmapWriter : IAidaMmapWriter
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly List<(int Offset, byte[] Data)> _writes = [];

        public IReadOnlyList<(int Offset, byte[] Data)> Writes => _writes;

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);
        public void Release() => _release.Set();

        public bool TryInit(out string? error)
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            error = null;
            return true;
        }

        public bool TryWrite(int offset, byte[] data, out string? error)
        {
            _writes.Add((offset, data));
            error = null;
            return true;
        }

        public void Dispose()
        {
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private static byte[]? WriteAt(FakeAidaMmapWriter writer, int offset)
    {
        foreach (var write in writer.Writes)
        {
            if (write.Offset == offset)
            {
                return write.Data;
            }
        }

        return null;
    }
}
