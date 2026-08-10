namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The named dynamic-query metric slots the frame-time producer reads. Each
/// enum value corresponds to one <see cref="PresentMonQuerySpec"/> in the
/// registration config; the <see cref="PresentMonQueryBuildResult.FieldIndexes"/>
/// map binds slots to element positions, so a registration change can never
/// silently reorder the reads.
/// </summary>
public enum DynamicField
{
    Fps,
    Low1PercentFps,
    GpuBusyMs,
    CpuFrameTimeMs,
    DisplayedFps,
    GpuTimeMs,
    DroppedFrames,
    PresentModeId,
}

/// <summary>One wanted dynamic-query metric: its field slot, metric id, and preferred stat.</summary>
public sealed record PresentMonQuerySpec(DynamicField Field, uint MetricId, uint PreferredStat);

/// <summary>
/// The registration outcome: the element array (in spec order, minus dropped
/// metrics), the field→element index map (-1 = metric unavailable), and the
/// human-readable descriptions of everything that was dropped.
/// </summary>
public sealed record PresentMonQueryBuildResult(
    PresentMonQueryElement[] Elements,
    int[] FieldIndexes,
    IReadOnlyList<string> DroppedMetrics);

/// <summary>
/// Builds the dynamic-query element array from the wanted specs against the
/// installed service's metric catalog. Pure and testable without the DLL: the
/// registration policy (metric availability, stat fallback, graceful
/// degradation of unsupported metrics) lives here instead of in hardcoded
/// mirrors, so an unregistrable metric degrades to a missing field with a
/// named reason rather than an opaque QueryMalformed failure.
/// </summary>
public static class PresentMonQueryBuilder
{
    public static PresentMonQueryBuildResult Build(IReadOnlyList<PresentMonQuerySpec> specs, PresentMonMetricCatalog catalog)
    {
        var elements = new List<PresentMonQueryElement>(specs.Count);
        var fieldIndexes = new int[Enum.GetValues<DynamicField>().Length];
        Array.Fill(fieldIndexes, -1);
        var dropped = new List<string>();

        foreach (var spec in specs)
        {
            if ((int)spec.Field >= fieldIndexes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(specs), $"DynamicField {spec.Field} has no registration slot.");
            }
            if (!catalog.TryGet((int)spec.MetricId, out var info))
            {
                dropped.Add($"metric {spec.MetricId} (field {spec.Field}) is not available in the installed service");
                continue;
            }
            if (info.AllowedStats.Count == 0)
            {
                dropped.Add($"metric {spec.MetricId} (field {spec.Field}) exposes no registrable stats");
                continue;
            }
            uint stat = info.AllowedStats.Contains((int)spec.PreferredStat)
                ? spec.PreferredStat
                : (uint)info.AllowedStats[0];
            fieldIndexes[(int)spec.Field] = elements.Count;
            elements.Add(new PresentMonQueryElement(spec.MetricId, stat, 0, 0, 0, 0));
        }

        return new PresentMonQueryBuildResult(elements.ToArray(), fieldIndexes, dropped);
    }
}
