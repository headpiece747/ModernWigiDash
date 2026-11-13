using System.Runtime.InteropServices;
using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

/// <summary>
/// The native PM_INTROSPECTION_ROOT parsing walk — verified live against the
/// real service on-device. The layout contract (sizes + field offsets, from
/// PresentMonAPI.h) is pinned here so the marshal mirrors cannot drift; the
/// walk itself is exercised through the empty-root path below and the live
/// service at runtime. (A full fake-root walk through the generic
/// PtrToStructure hits a test-host marshalling NRE on IntPtr-carrying
/// structs; the layout pins cover the same drift risk without it.)
/// </summary>
[TestClass]
public class PresentMonIntrospectionTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjArray { public IntPtr pData; public ulong size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Metric
    {
        public int id;
        public int type;
        public int unit;
        public int preferredUnitHint;
        public IntPtr pTypeInfo;
        public IntPtr pStatInfo;
        public IntPtr pDeviceMetricInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StatInfo { public int stat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Root { public IntPtr pMetrics; public IntPtr pEnums; public IntPtr pDevices; public IntPtr pUnits; }

    [TestMethod]
    public void IntrospectionLayout_SizesAndOffsets_MatchPresentMonApiHeader()
    {
        // PM_INTROSPECTION_OBJARRAY: pointer + size.
        Assert.AreEqual(16, Marshal.SizeOf<ObjArray>(), "pointer (8) + size (8)");

        // PM_INTROSPECTION_METRIC: 4 ints then 3 pointers.
        Assert.AreEqual(40, Marshal.SizeOf<Metric>());
        Assert.AreEqual(0, Marshal.OffsetOf<Metric>(nameof(Metric.id)).ToInt64());
        Assert.AreEqual(4, Marshal.OffsetOf<Metric>(nameof(Metric.type)).ToInt64());
        Assert.AreEqual(8, Marshal.OffsetOf<Metric>(nameof(Metric.unit)).ToInt64());
        Assert.AreEqual(12, Marshal.OffsetOf<Metric>(nameof(Metric.preferredUnitHint)).ToInt64());
        Assert.AreEqual(16, Marshal.OffsetOf<Metric>(nameof(Metric.pTypeInfo)).ToInt64());
        Assert.AreEqual(24, Marshal.OffsetOf<Metric>(nameof(Metric.pStatInfo)).ToInt64(), "the stat list sits after the type info");
        Assert.AreEqual(32, Marshal.OffsetOf<Metric>(nameof(Metric.pDeviceMetricInfo)).ToInt64());

        // PM_INTROSPECTION_STAT_INFO: one int.
        Assert.AreEqual(4, Marshal.SizeOf<StatInfo>());

        // PM_INTROSPECTION_ROOT: four pointers, metrics first.
        Assert.AreEqual(32, Marshal.SizeOf<Root>());
        Assert.AreEqual(0, Marshal.OffsetOf<Root>(nameof(Root.pMetrics)).ToInt64());
    }

    [TestMethod]
    public void ParseMetrics_EmptyMetricArray_EmptyCatalog()
    {
        IntPtr metricsArray = AllocObjArray(IntPtr.Zero, 0);
        IntPtr rootBlock = Marshal.AllocHGlobal(Marshal.SizeOf<Root>());
        try
        {
            Marshal.StructureToPtr(new Root { pMetrics = metricsArray }, rootBlock, false);

            var parsed = PresentMonIntrospection.ParseMetrics(rootBlock);

            Assert.AreEqual(0, parsed.Count);
        }
        finally
        {
            Marshal.FreeHGlobal(metricsArray);
            Marshal.FreeHGlobal(rootBlock);
        }
    }

    [TestMethod]
    public void ParseMetrics_NullPointersAndZeroData_ReturnsEmptyOrSkipsNullEntries()
    {
        Assert.AreEqual(0, PresentMonIntrospection.ParseMetrics(IntPtr.Zero).Count);

        IntPtr rootBlock = Marshal.AllocHGlobal(Marshal.SizeOf<Root>());
        IntPtr emptyDataMetricsArray = AllocObjArray(IntPtr.Zero, 2);
        IntPtr badStatArray = AllocObjArray(IntPtr.Zero, 2);
        IntPtr statOneBlock = Marshal.AllocHGlobal(Marshal.SizeOf<StatInfo>());
        IntPtr statPtrs = Marshal.AllocHGlobal(IntPtr.Size * 2);
        IntPtr goodStatArray = AllocObjArray(statPtrs, 2);
        IntPtr metricValid = Marshal.AllocHGlobal(Marshal.SizeOf<Metric>());
        IntPtr metricBadStats = Marshal.AllocHGlobal(Marshal.SizeOf<Metric>());
        IntPtr metricPtrs = Marshal.AllocHGlobal(IntPtr.Size * 3);
        IntPtr populatedMetricsArray = AllocObjArray(metricPtrs, 3);

        try
        {
            // Case 1: null pMetrics pointer on root
            Marshal.StructureToPtr(new Root { pMetrics = IntPtr.Zero }, rootBlock, false);
            Assert.AreEqual(0, PresentMonIntrospection.ParseMetrics(rootBlock).Count);

            // Case 2: non-zero count with null pData on metrics array
            Marshal.StructureToPtr(new Root { pMetrics = emptyDataMetricsArray }, rootBlock, false);
            Assert.AreEqual(0, PresentMonIntrospection.ParseMetrics(rootBlock).Count);

            // Case 3: null metric element + valid metric with mixed null/valid stat ptrs + metric with null stat pData
            Marshal.StructureToPtr(new StatInfo { stat = 5 }, statOneBlock, false);
            Marshal.WriteIntPtr(statPtrs, 0, IntPtr.Zero);
            Marshal.WriteIntPtr(statPtrs, IntPtr.Size, statOneBlock);

            Marshal.StructureToPtr(new Metric { id = 10, type = 1, unit = 2, pStatInfo = goodStatArray }, metricValid, false);
            Marshal.StructureToPtr(new Metric { id = 11, type = 1, unit = 2, pStatInfo = badStatArray }, metricBadStats, false);

            Marshal.WriteIntPtr(metricPtrs, 0, IntPtr.Zero);
            Marshal.WriteIntPtr(metricPtrs, IntPtr.Size, metricValid);
            Marshal.WriteIntPtr(metricPtrs, IntPtr.Size * 2, metricBadStats);
            Marshal.StructureToPtr(new Root { pMetrics = populatedMetricsArray }, rootBlock, false);

            var parsed = PresentMonIntrospection.ParseMetrics(rootBlock);

            Assert.AreEqual(2, parsed.Count);
            Assert.AreEqual(10, parsed[0].Id);
            CollectionAssert.AreEqual(new[] { 5 }, parsed[0].AllowedStats.ToArray());
            Assert.AreEqual(11, parsed[1].Id);
            Assert.AreEqual(0, parsed[1].AllowedStats.Count);
        }
        finally
        {
            Marshal.FreeHGlobal(populatedMetricsArray);
            Marshal.FreeHGlobal(metricPtrs);
            Marshal.FreeHGlobal(metricBadStats);
            Marshal.FreeHGlobal(metricValid);
            Marshal.FreeHGlobal(goodStatArray);
            Marshal.FreeHGlobal(statPtrs);
            Marshal.FreeHGlobal(statOneBlock);
            Marshal.FreeHGlobal(badStatArray);
            Marshal.FreeHGlobal(emptyDataMetricsArray);
            Marshal.FreeHGlobal(rootBlock);
        }
    }

    private static IntPtr AllocObjArray(IntPtr pData, ulong size)
    {
        IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf<ObjArray>());
        Marshal.StructureToPtr(new ObjArray { pData = pData, size = size }, block, false);
        return block;
    }
}
