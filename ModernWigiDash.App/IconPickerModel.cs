using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// The icon picker's decision model: the search filter over the Griddy icon
/// catalog, the named-or-custom selection, the custom chip text, the
/// highlight, and the accept verdict. Pure: the picker window
/// (<see cref="DialogHost.ShowIconPicker"/>) is a thin adapter over it, and
/// tests drive the same rules without a window.
/// </summary>
internal sealed class IconPickerModel
{
    private string _chosen;
    private IReadOnlyCollection<string> _visibleNames;

    public IconPickerModel(string? currentValue)
    {
        _chosen = currentValue ?? "";
        _visibleNames = GriddyIcons.Names;
    }

    /// <summary>The icons the grid shows: every catalog name when the filter
    /// is blank, else the names containing the filter (case-insensitive).</summary>
    public IReadOnlyCollection<string> VisibleNames => _visibleNames;

    /// <summary>The chosen value: a catalog name or a custom SVG path.</summary>
    public string Chosen => _chosen;

    /// <summary>
    /// The custom chip text: the chosen value's <c>Custom: {path}</c> spelling
    /// when it is a custom SVG path, else empty. The chip follows the
    /// selection, so a named pick never leaves a stale custom label behind.
    /// </summary>
    public string ChipText => IconValuePolicy.IsCustom(_chosen) ? CustomChipText(_chosen) : "";

    /// <summary>The one spelling of the custom chip text.</summary>
    public static string CustomChipText(string customPath) => $"Custom: {customPath}";

    /// <summary>True when the cell's icon matches the current selection (case-insensitive).</summary>
    public bool IsHighlighted(string name) => name.Equals(_chosen, StringComparison.OrdinalIgnoreCase);

    /// <summary>The accept verdict: the chosen value, or null when it is blank (the Select button stays a no-op).</summary>
    public string? Accept() => string.IsNullOrWhiteSpace(_chosen) ? null : _chosen;

    /// <summary>Applies the search box's text as the filter and recomputes the visible names.</summary>
    public void UpdateSearch(string? text)
    {
        string filter = text?.Trim() ?? "";
        _visibleNames = string.IsNullOrEmpty(filter)
            ? GriddyIcons.Names
            : GriddyIcons.Names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Selects a value: a clicked catalog name or a copied custom SVG path.</summary>
    public void Select(string value) => _chosen = value;
}
