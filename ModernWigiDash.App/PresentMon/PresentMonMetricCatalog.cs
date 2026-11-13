namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// One metric's truth as reported by the installed PresentMon service's
/// runtime introspection (PM_INTROSPECTION_METRIC): its type, unit, and the
/// stats the service accepts for it in a dynamic query. Registration policy
/// consults this instead of hand-maintained mirrors, so an installed service
/// that differs from the header never silently mis-registers.
/// </summary>
internal sealed record PresentMonMetricInfo(
    int Id,
    int MetricType,
    int Unit,
    IReadOnlyList<int> AllowedStats);

/// <summary>
/// The metrics the installed PresentMon service exposes, keyed by metric id.
/// Built once per session from <c>pmGetIntrospectionRoot</c>
/// (<see cref="PresentMonIntrospection"/>); consumed by
/// <see cref="PresentMonQueryBuilder"/> to build the dynamic query.
/// </summary>
internal sealed class PresentMonMetricCatalog
{
    private readonly IReadOnlyDictionary<int, PresentMonMetricInfo> _metrics;

    public PresentMonMetricCatalog(IEnumerable<PresentMonMetricInfo> metrics)
        => _metrics = BuildLookup(metrics);

    private static Dictionary<int, PresentMonMetricInfo> BuildLookup(IEnumerable<PresentMonMetricInfo> metrics)
    {
        var lookup = new Dictionary<int, PresentMonMetricInfo>();
        foreach (var metric in metrics)
        {
            if (!lookup.TryAdd(metric.Id, metric) &&
                lookup[metric.Id].AllowedStats.Count == 0 &&
                metric.AllowedStats.Count > 0)
            {
                lookup[metric.Id] = metric;
            }
        }
        return lookup;
    }

    public bool TryGet(int metricId, out PresentMonMetricInfo info)
    {
        if (_metrics.TryGetValue(metricId, out var found))
        {
            info = found;
            return true;
        }
        info = default!;
        return false;
    }
}
