namespace ModernWigiDash.Core.Models;

/// <summary>
/// One page of the profile: its background, the grid-snap flag, and the
/// widgets placed on it.
/// </summary>
public class PageLayout
{
    /// <summary>The one default page background — the compositor's parse
    /// fallback references this, so the fallback and the default can never
    /// drift apart.</summary>
    public const string DefaultBackgroundHexColor = "#12141D";

    // Export-schema surface: written by ExportJson, never read back by ImportJson.
    /// <summary>The page's stable identity in the export schema (GUID); never read back on import.</summary>
    public string PageId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>The page's display name; a blank assignment repairs to the "Main Dashboard" default.</summary>
    public string PageName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Main Dashboard" : value.Trim(); } = "Main Dashboard";
    /// <summary>The page's background color as a #RRGGBB hex string; a blank assignment repairs to <see cref="DefaultBackgroundHexColor"/>.</summary>
    public string BackgroundHexColor { get => field; set => field = string.IsNullOrWhiteSpace(value) ? DefaultBackgroundHexColor : value.Trim(); } = DefaultBackgroundHexColor;
    /// <summary>Optional page background image path (relative, sanitized on import).</summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>Whether widget placement on this page snaps to the design grid.</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>The widgets placed on this page.</summary>
    public List<PlacedWidgetInstance> Widgets { get; set; } = [];
}

/// <summary>
/// The persisted profile: its pages, the active page index, and the
/// page-range invariant enforced at the Pages boundary.
/// </summary>
public class ProfileLayout
{
    // Export-schema surface: written by ExportJson, never read back by ImportJson.
    /// <summary>The profile's stable identity in the export schema (GUID); never read back on import.</summary>
    public string ProfileId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>The profile's display name; a blank assignment repairs to the "Default Profile" default.</summary>
    public string ProfileName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Default Profile" : value.Trim(); } = "Default Profile";
    /// <summary>
    /// The profile's pages. The non-empty invariant is enforced at this
    /// boundary — a null or empty assignment repairs to a single default
    /// page (the same repair rule the <see cref="PageLayout.PageName"/> and
    /// <see cref="PageLayout.BackgroundHexColor"/> setters apply) — so
    /// <see cref="ActivePage"/> is total and the old orphan-page fallback is
    /// unrepresentable. In-place removal stays safe: ProfileOps refuses to
    /// delete the last page, and the import sanitizer repairs untrusted input
    /// before it reaches this boundary.
    /// </summary>
    public List<PageLayout> Pages
    {
        get => field;
        set => field = value is { Count: > 0 } ? value : [new PageLayout()];
    } = [new PageLayout()];

    /// <summary>
    /// The active page index, clamped to the page range: never negative and
    /// never past the last page (the profile's invariants — the ctor creates
    /// one page, ProfileOps refuses to delete the last one, the import
    /// sanitizer guarantees at least one — keep <see cref="Pages"/> non-empty).
    /// </summary>
    public int ActivePageIndex
    {
        get => field;
        set => field = Pages.Count > 0 ? Math.Clamp(value, 0, Pages.Count - 1) : 0;
    }

    /// <summary>
    /// The active page — total by construction: <see cref="Pages"/> is never
    /// empty (the setter repairs, ProfileOps refuses to delete the last page,
    /// the import sanitizer repairs untrusted input) and the index is
    /// clamped on write, re-clamped on read, so the old orphan-page fallback
    /// (a detached page whose mutations were silently lost) is unrepresentable.
    /// A violation of the non-empty invariant fails loudly
    /// (IndexOutOfRangeException) instead of fabricating a page.
    /// </summary>
    public PageLayout ActivePage => Pages[Math.Clamp(ActivePageIndex, 0, Pages.Count - 1)];

    /// <summary>
    /// The window's close behavior as the raw persisted value (the
    /// <see cref="CloseBehaviorPolicy"/> vocabulary: "quit" | "hideToTray").
    /// Null when this profile has no opinion — the pre-feature and
    /// hand-crafted shapes; the runtime read resolves through
    /// <see cref="CloseBehaviorPolicy.Resolve"/> (null → the default Quit).
    /// The settings dialog is the one writer; the untrusted-import rule
    /// (absent stays absent, present-but-corrupt normalizes to
    /// <see cref="CloseBehaviorPolicy.Quit"/>) lives in
    /// <see cref="ProfileImportSanitizer"/>; the import merge (an imported
    /// profile lacking the field keeps the local value) runs in the import
    /// flow (ProfileImportFlow) so the next export carries it.
    /// </summary>
    public string? CloseBehavior { get; set; }
}
