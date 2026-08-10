using System.Windows;

namespace ModernWigiDash.App;

/// <summary>
/// The pure layout rules for one page tab: the padding/margin/geometry values
/// the tab strip bakes into its buttons, derived from the tab's active and
/// delete state. No UI tree — the constants are pinned by tests.
/// </summary>
internal readonly record struct PageTabVisual(PageTabItem Item)
{
    /// <summary>Whether the tab is the active page (accent styling, white icons).</summary>
    public bool IsActive => Item.IsActive;

    /// <summary>Whether the tab shows a close button (delete allowed).</summary>
    public bool CanDelete => Item.CanDelete;

    /// <summary>Tab-button padding: the right inset leaves room for the icon
    /// buttons stacked at the tab's right edge (larger when a close button
    /// also sits there).</summary>
    public Thickness TabPadding => new(14, 6, CanDelete ? 56 : 42, 6);

    /// <summary>Rename-icon right margin: clears the close button when one
    /// exists, sits snug otherwise.</summary>
    public Thickness RenameIconMargin => new(0, 0, CanDelete ? 24 : 4, 0);

    /// <summary>Close-icon right margin (the tab's strip-edge side).</summary>
    public Thickness CloseIconMargin => new(0, 0, 4, 0);

    /// <summary>The 20×20 icon-button geometry shared by the rename and close buttons.</summary>
    public static double IconSize { get; } = 20;

    /// <summary>The icon glyph size inside the icon buttons.</summary>
    public static double IconFontSize { get; } = 10;
}
