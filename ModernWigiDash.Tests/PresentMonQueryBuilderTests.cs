using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

/// <summary>
/// The registration policy behind the PresentMon dynamic query: the catalog
/// supplies the installed service's truth, the builder validates every wanted
/// metric/stat against it, and unsupported metrics degrade to a named drop
/// instead of an opaque QueryMalformed failure.
/// </summary>
[TestClass]
public class PresentMonQueryBuilderTests
{
    private static PresentMonMetricCatalog Catalog(params PresentMonMetricInfo[] metrics) => new(metrics);

    private static PresentMonMetricInfo Metric(int id, params int[] allowedStats)
        => new(id, MetricType: 0, Unit: 0, allowedStats);

    [TestMethod]
    public void Build_PreferredStatAllowed_UsesPreferredStat()
    {
        var catalog = Catalog(Metric(12, 1, 2, 5), Metric(14, 1));
        var specs = new[]
        {
            new PresentMonQuerySpec(DynamicField.Fps, 12, 1),
            new PresentMonQuerySpec(DynamicField.GpuBusyMs, 14, 1),
        };

        var result = PresentMonQueryBuilder.Build(specs, catalog);

        Assert.AreEqual(2, result.Elements.Length);
        Assert.AreEqual(1u, result.Elements[0].Stat);
        Assert.AreEqual(1u, result.Elements[1].Stat);
        Assert.AreEqual(0, result.DroppedMetrics.Count);
    }

    [TestMethod]
    public void Build_StatNotAllowed_FallsBackToFirstAllowedStat()
    {
        // PRESENT_MODE on a service that allows only NEWEST_POINT/MID_LERP:
        // a preferred AVG must fall back instead of failing registration.
        var catalog = Catalog(Metric(20, 12, 10));

        var result = PresentMonQueryBuilder.Build(
            [new PresentMonQuerySpec(DynamicField.PresentModeId, 20, 1)], catalog);

        Assert.AreEqual(1, result.Elements.Length);
        Assert.AreEqual(12u, result.Elements[0].Stat, "the first allowed stat replaces the rejected preferred stat");
        Assert.AreEqual(0, result.DroppedMetrics.Count);
    }

    [TestMethod]
    public void Build_MissingMetric_DropsFieldWithDescription()
    {
        var catalog = Catalog(Metric(12, 1));

        var result = PresentMonQueryBuilder.Build(
            [new PresentMonQuerySpec(DynamicField.PresentModeId, 20, 12)], catalog);

        Assert.AreEqual(0, result.Elements.Length);
        Assert.AreEqual(-1, result.FieldIndexes[(int)DynamicField.PresentModeId], "a dropped metric's field reads as no-data");
        Assert.AreEqual(1, result.DroppedMetrics.Count);
        StringAssert.Contains(result.DroppedMetrics[0], "20");
        StringAssert.Contains(result.DroppedMetrics[0], "PresentModeId");
    }

    [TestMethod]
    public void Build_MetricWithNoStats_DropsField()
    {
        var catalog = Catalog(Metric(20));

        var result = PresentMonQueryBuilder.Build(
            [new PresentMonQuerySpec(DynamicField.PresentModeId, 20, 12)], catalog);

        Assert.AreEqual(0, result.Elements.Length);
        Assert.AreEqual(-1, result.FieldIndexes[(int)DynamicField.PresentModeId]);
        Assert.AreEqual(1, result.DroppedMetrics.Count);
    }

    [TestMethod]
    public void Build_AllPresent_FieldIndexesFollowSpecOrder()
    {
        var catalog = Catalog(Metric(12, 1, 5), Metric(14, 1), Metric(8, 1), Metric(11, 1), Metric(13, 1), Metric(16, 1), Metric(20, 12));
        var specs = new[]
        {
            new PresentMonQuerySpec(DynamicField.Fps, 12, 1),
            new PresentMonQuerySpec(DynamicField.Low1PercentFps, 12, 5),
            new PresentMonQuerySpec(DynamicField.GpuBusyMs, 14, 1),
            new PresentMonQuerySpec(DynamicField.CpuFrameTimeMs, 8, 1),
            new PresentMonQuerySpec(DynamicField.DisplayedFps, 11, 1),
            new PresentMonQuerySpec(DynamicField.GpuTimeMs, 13, 1),
            new PresentMonQuerySpec(DynamicField.DroppedFrames, 16, 1),
            new PresentMonQuerySpec(DynamicField.PresentModeId, 20, 12),
        };

        var result = PresentMonQueryBuilder.Build(specs, catalog);

        Assert.AreEqual(8, result.Elements.Length);
        for (int i = 0; i < 8; i++)
        {
            Assert.AreEqual(i, result.FieldIndexes[i], $"field slot {i} must map to element {i} in spec order");
        }
    }

    [TestMethod]
    public void DynamicQuerySpecs_EveryDynamicField_RegisteredExactlyOnce()
    {
        var fields = Enum.GetValues<DynamicField>();
        var registered = PresentMonNative.DynamicQuerySpecs.Select(s => s.Field).ToArray();

        Assert.AreEqual(fields.Length, registered.Length, "the registration config must cover every field slot");
        CollectionAssert.AreEquivalent(fields.ToArray(), registered,
            "every field slot must be registered exactly once — a gap or duplicate would silently misread blobs");
    }
}
