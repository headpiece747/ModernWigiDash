using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

/// <summary>
/// The query subsystem's stateful loops, driven through fake delegates that
/// simulate the service: registration (which fills element offsets/sizes),
/// the poll capacity-growth loop, and the frame drain loop.
/// </summary>
[TestClass]
public class PresentMonQueryRegistryTests
{
    private const uint MetricPresentMode = 20;

    private sealed class FakeService
    {
        public PresentMonQueryElement[]? RegisteredDynamic;
        public PresentMonQueryElement[]? RegisteredFrame;
        public List<uint> PollCapacities = [];
        public PmStatus PollStatus = PmStatus.Success;
        public int InsufficientBufferThenSuccess;
        public uint NoDataChains = 1;
        public List<uint> ConsumeRequests = [];
        public uint ConsumeBatches;
        public PmStatus RegisterDynamicStatus = PmStatus.Success;
        public PresentMonMetricCatalog? Catalog = new PresentMonMetricCatalog(CatalogMetrics());

        private void FillOffsets(PresentMonQueryElement[] elements)
        {
            ulong offset = 0;
            for (int i = 0; i < elements.Length; i++)
            {
                ulong size = elements[i].Metric == MetricPresentMode ? 4UL : 8UL;
                elements[i] = elements[i] with { DataOffset = offset, DataSize = size };
                offset += size;
            }
        }

        private void WriteChain(byte[] blob, PresentMonQueryElement[] elements, Func<int, double> valueForMetric)
        {
            foreach (var e in elements)
            {
                double value = valueForMetric((int)e.Metric);
                if (e.DataSize == 4)
                {
                    BitConverter.GetBytes((int)value).CopyTo(blob, (int)e.DataOffset);
                }
                else
                {
                    BitConverter.GetBytes(value).CopyTo(blob, (int)e.DataOffset);
                }
            }
        }

        public PresentMonQueryRegistry CreateRegistry()
        {
            return new PresentMonQueryRegistry(
                registerDynamic: (IntPtr session, out IntPtr handle, PresentMonQueryElement[] elements, ulong count, double window, double offset) =>
                {
                    // The real service mutates the passed array in place
                    // ([In, Out]) — offsets/sizes must land in the registry's
                    // own copy, so no Clone here.
                    RegisteredDynamic = elements;
                    FillOffsets(elements);
                    handle = (IntPtr)1;
                    return RegisterDynamicStatus;
                },
                freeDynamic: (IntPtr handle) => PmStatus.Success,
                pollDynamic: (IntPtr handle, uint pid, byte[] blob, ref uint chains) =>
                {
                    PollCapacities.Add(chains);
                    if (InsufficientBufferThenSuccess > 0)
                    {
                        InsufficientBufferThenSuccess--;
                        return PmStatus.InsufficientBuffer; // chains keeps its declared capacity; the registry doubles and retries
                    }
                    if (PollStatus != PmStatus.Success)
                    {
                        return PollStatus;
                    }
                    chains = NoDataChains;
                    if (chains > 0)
                    {
                        WriteChain(blob, RegisteredDynamic!, m => m == MetricPresentMode ? 8 : 100.0);
                    }
                    return PmStatus.Success;
                },
                registerFrame: (IntPtr session, out IntPtr handle, PresentMonQueryElement[] elements, ulong count, out uint blobSize) =>
                {
                    RegisteredFrame = elements;
                    FillOffsets(elements);
                    handle = (IntPtr)2;
                    blobSize = 8;
                    return PmStatus.Success;
                },
                consumeFrames: (IntPtr handle, uint pid, byte[] blobs, ref uint framesToRead) =>
                {
                    ConsumeRequests.Add(framesToRead);
                    uint batch = ConsumeBatches;
                    for (uint i = 0; i < batch; i++)
                    {
                        BitConverter.GetBytes(6.5 + i * 0.01).CopyTo(blobs, (int)(i * 8));
                    }
                    framesToRead = batch;
                    ConsumeBatches = 0;
                    return PmStatus.Success;
                },
                freeFrame: (IntPtr handle) => PmStatus.Success,
                readCatalog: (IntPtr session) => Catalog);
        }
    }

    private static PresentMonMetricInfo[] CatalogMetrics() =>
    [
        new(8, 3, 6, [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 10]),
        new(11, 0, 4, [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 10]),
        new(12, 0, 4, [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 10]),
        new(13, 3, 6, [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 10]),
        new(14, 3, 6, [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 10]),
        new(16, 3, 0, [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 10]),
        new(20, 3, 0, [12, 10]),
    ];

    [TestMethod]
    public void Register_ValidCatalog_RegistersDynamicAndFrameQueries()
    {
        var service = new FakeService();
        var registry = service.CreateRegistry();

        Assert.IsTrue(registry.Register((IntPtr)9, out string? reason));
        Assert.IsNull(reason);

        Assert.AreEqual(8, service.RegisteredDynamic!.Length);
        Assert.AreEqual(12u, service.RegisteredDynamic[0].Metric, "Presented FPS first");
        Assert.AreEqual(1u, service.RegisteredDynamic[0].Stat);
        Assert.AreEqual(20u, service.RegisteredDynamic[7].Metric, "Present Mode last");
        Assert.AreEqual(12u, service.RegisteredDynamic[7].Stat, "NEWEST_POINT picked from the service's allowed stats");
        Assert.AreEqual(4, (int)service.RegisteredDynamic[7].DataSize, "the enum element is 4 bytes");
        Assert.AreEqual(1, service.RegisteredFrame!.Length);
        Assert.AreEqual(78u, service.RegisteredFrame[0].Metric, "Between Presents frame query");
    }

    [TestMethod]
    public void Register_CatalogUnavailable_ReportsIntrospectionFailure()
    {
        var service = new FakeService { Catalog = null };
        var registry = service.CreateRegistry();

        Assert.IsFalse(registry.Register((IntPtr)9, out string? reason));
        StringAssert.Contains(reason, "catalog");
    }

    [TestMethod]
    public void Register_DynamicRegistrationRejected_ReportsStatusAndDroppedMetrics()
    {
        // Catalog without PRESENT_MODE so the builder drops a field: the
        // failure message must name both the status and the dropped metric.
        var service = new FakeService
        {
            RegisterDynamicStatus = PmStatus.QueryMalformed,
            Catalog = new PresentMonMetricCatalog(CatalogMetrics().Where(m => m.Id != 20)),
        };
        var registry = service.CreateRegistry();

        Assert.IsFalse(registry.Register((IntPtr)9, out string? reason));
        StringAssert.Contains(reason, "QueryMalformed");
        StringAssert.Contains(reason, "PresentModeId", "the dropped metric's field is named in the failure");
    }

    [TestMethod]
    public void PollDynamic_InsufficientBuffer_GrowsCapacityAndReadsSample()
    {
        var service = new FakeService { InsufficientBufferThenSuccess = 1 };
        var registry = service.CreateRegistry();
        Assert.IsTrue(registry.Register((IntPtr)9, out _));

        var result = registry.PollDynamic(4321);

        CollectionAssert.AreEqual(new uint[] { 32, 64 }, service.PollCapacities.ToArray(),
            "the first poll declares 32 chains, the retry doubles to 64");
        Assert.IsNotNull(result.Sample);
        Assert.AreEqual(100.0, result.Sample.Fps, 0.001);
        Assert.AreEqual(8, result.Sample.PresentModeId, "the 4-byte enum element reads as its int32 id");
    }

    [TestMethod]
    public void PollDynamic_NoSwapChains_NullSampleWithSuccess()
    {
        var service = new FakeService { NoDataChains = 0 };
        var registry = service.CreateRegistry();
        Assert.IsTrue(registry.Register((IntPtr)9, out _));

        var result = registry.PollDynamic(4321);

        Assert.IsNull(result.Sample);
        Assert.AreEqual(PmStatus.Success, result.Status, "no data yet is a benign poll, not a session failure");
    }

    [TestMethod]
    public void PollDynamic_DroppedField_ReadsZero()
    {
        // Catalog without PRESENT_MODE: the field is dropped at registration
        // and every poll reads it as 0 (no-data) instead of misreading the blob.
        var service = new FakeService
        {
            Catalog = new PresentMonMetricCatalog(CatalogMetrics().Where(m => m.Id != 20)),
        };
        var registry = service.CreateRegistry();
        Assert.IsTrue(registry.Register((IntPtr)9, out _));

        var result = registry.PollDynamic(4321);

        Assert.AreEqual(7, service.RegisteredDynamic!.Length);
        Assert.IsNotNull(result.Sample);
        Assert.AreEqual(0, result.Sample.PresentModeId);
        Assert.AreEqual(100.0, result.Sample.Fps, 0.001, "the remaining fields still read correctly");
    }

    [TestMethod]
    public void DrainFrameTimes_LoopsUntilFewerThanCapacity()
    {
        var service = new FakeService { ConsumeBatches = 256 };
        var registry = service.CreateRegistry();
        Assert.IsTrue(registry.Register((IntPtr)9, out _));

        service.ConsumeBatches = 256;
        var first = registry.DrainFrameTimes(4321);
        Assert.AreEqual(256, first.Count, "a full-capacity batch drains completely");
        service.ConsumeBatches = 256;
        var second = registry.DrainFrameTimes(4321);
        Assert.AreEqual(256, second.Count, "the pooled result list is refilled per drain");
        service.ConsumeBatches = 2;
        var tail = registry.DrainFrameTimes(4321);

        Assert.AreEqual(2, tail.Count, "a short final batch ends the drain loop");
        Assert.AreEqual(6.51, tail[1], 0.001);
        CollectionAssert.AreEqual(new uint[] { 256, 256, 256, 256, 256 }, service.ConsumeRequests.ToArray());
    }

    [TestMethod]
    public void Free_ReleasesQueryHandles()
    {
        var service = new FakeService();
        var registry = service.CreateRegistry();
        Assert.IsTrue(registry.Register((IntPtr)9, out _));

        registry.Free();
        // No exception and subsequent polls are benign no-data — the handles are gone.
        var result = registry.PollDynamic(4321);
        Assert.IsNull(result.Sample);
        Assert.AreEqual(PmStatus.Success, result.Status);
    }
}
