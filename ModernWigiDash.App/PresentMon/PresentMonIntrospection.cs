using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Parses the native PM_INTROSPECTION_ROOT tree (from
/// <c>pmGetIntrospectionRoot</c>) into the managed <see cref="PresentMonMetricCatalog"/>
/// model. Layout mirrors PresentMonAPI.h and is validated against the real
/// service; the caller frees the native root after parsing.
/// </summary>
internal static class PresentMonIntrospection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PmIntrospectionObjArray
    {
        public IntPtr pData;
        public ulong size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PmIntrospectionMetric
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
    private struct PmIntrospectionStatInfo
    {
        public int stat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PmIntrospectionRoot
    {
        public IntPtr pMetrics;
        public IntPtr pEnums;
        public IntPtr pDevices;
        public IntPtr pUnits;
    }

    public static IReadOnlyList<PresentMonMetricInfo> ParseMetrics(IntPtr rootPtr)
    {
        if (rootPtr == IntPtr.Zero)
        {
            return [];
        }

        var root = Marshal.PtrToStructure<PmIntrospectionRoot>(rootPtr);
        if (root.pMetrics == IntPtr.Zero)
        {
            return [];
        }

        var metricsArray = Marshal.PtrToStructure<PmIntrospectionObjArray>(root.pMetrics);
        if (metricsArray.size == 0 || metricsArray.pData == IntPtr.Zero)
        {
            return [];
        }

        var result = new List<PresentMonMetricInfo>(checked((int)metricsArray.size));
        for (ulong i = 0; i < metricsArray.size; i++)
        {
            IntPtr metricPtr = Marshal.ReadIntPtr(metricsArray.pData, checked((int)i * IntPtr.Size));
            if (metricPtr == IntPtr.Zero)
            {
                continue;
            }

            var metric = Marshal.PtrToStructure<PmIntrospectionMetric>(metricPtr);
            result.Add(new PresentMonMetricInfo(
                Id: metric.id,
                MetricType: metric.type,
                Unit: metric.unit,
                AllowedStats: ReadStats(metric.pStatInfo)));
        }

        return result;
    }

    private static IReadOnlyList<int> ReadStats(IntPtr statInfoPtr)
    {
        if (statInfoPtr == IntPtr.Zero)
        {
            return [];
        }

        var array = Marshal.PtrToStructure<PmIntrospectionObjArray>(statInfoPtr);
        if (array.size == 0 || array.pData == IntPtr.Zero)
        {
            return [];
        }

        var stats = new List<int>(checked((int)array.size));
        for (ulong i = 0; i < array.size; i++)
        {
            IntPtr statPtr = Marshal.ReadIntPtr(array.pData, checked((int)i * IntPtr.Size));
            if (statPtr == IntPtr.Zero)
            {
                continue;
            }

            stats.Add(Marshal.PtrToStructure<PmIntrospectionStatInfo>(statPtr).stat);
        }

        return stats;
    }
}
