using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.App;

/// <summary>
/// The widget-catalog filter and sort used by the window's catalog list —
/// pure so the match rules are assertable without WPF.
/// </summary>
internal static class CatalogFilter
{
    public static IReadOnlyList<PluginInfo> Apply(IEnumerable<PluginInfo> plugins, string query)
    {
        IEnumerable<PluginInfo> source = plugins;
        if (!string.IsNullOrEmpty(query))
        {
            source = source.Where(p =>
                p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        return source.OrderBy(p => p.DisplayName).ToList();
    }
}
