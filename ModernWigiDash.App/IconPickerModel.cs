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

    /// <summary>The accept verdict: the chosen value, or null when it is blank (the Select button stays a no-op). The custom-file shape is pinned by test: a non-blank value that is not a catalog name is accepted as-is, so the picker can never smuggle in an empty or whitespace-only path.</summary>
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

    /// <summary>The one refusal message for an unsupported SVG (the host's error dialog renders it).</summary>
    public const string UnsupportedSvgMessage = "Only single-path SVG icons are supported.";

    /// <summary>The SVG-browse verdict: what the picker did with a browsed file.</summary>
    /// <param name="Accepted">True when the file was accepted and selected.</param>
    /// <param name="SelectedPath">The copied icon path when accepted, else null.</param>
    /// <param name="RefusalMessage">The refusal text when rejected, else null.</param>
    public sealed record SvgBrowseVerdict(bool Accepted, string? SelectedPath, string? RefusalMessage);

    /// <summary>
    /// The SVG-browse decision: validates the picked file as a single-path SVG,
    /// copies it into the icons folder, and selects it. Pure over the injected
    /// validate/copy seams so the rule is assertable without a file dialog or
    /// the filesystem; the host keeps only the dialog chrome and the refusal
    /// error dialog. A non-single-path file refuses (and never copies), so an
    /// unsupported pick cannot be smuggled in as a custom icon.
    /// </summary>
    /// <param name="sourcePath">The picked file's path.</param>
    /// <param name="tryValidate">Validates the file as a single-path SVG.</param>
    /// <param name="copyToIcons">Copies the file into the icons folder, returning the new path.</param>
    /// <returns>The browse verdict.</returns>
    public SvgBrowseVerdict BrowseSvg(string sourcePath, Func<string, bool> tryValidate, Func<string, string> copyToIcons)
    {
        if (!tryValidate(sourcePath))
            return new SvgBrowseVerdict(false, null, UnsupportedSvgMessage);
        string copied = copyToIcons(sourcePath);
        Select(copied);
        return new SvgBrowseVerdict(true, copied, null);
    }
}
