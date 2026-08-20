namespace ModernWigiDash.Core.Models;

public class PageLayout
{
    /// <summary>The one default page background — the compositor's parse
    /// fallback references this, so the fallback and the default can never
    /// drift apart.</summary>
    public const string DefaultBackgroundHexColor = "#12141D";

    // Export-schema surface: written by ExportJson, never read back by ImportJson.
    public string PageId { get; set; } = Guid.NewGuid().ToString();
    public string PageName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Main Dashboard" : value.Trim(); } = "Main Dashboard";
    public string BackgroundHexColor { get => field; set => field = string.IsNullOrWhiteSpace(value) ? DefaultBackgroundHexColor : value.Trim(); } = DefaultBackgroundHexColor;
    public string BackgroundImagePath { get; set; } = string.Empty;

    public bool SnapToGrid { get; set; } = true;

    public List<PlacedWidgetInstance> Widgets { get; set; } = [];
}

public class ProfileLayout
{
    // Export-schema surface: written by ExportJson, never read back by ImportJson.
    public string ProfileId { get; set; } = Guid.NewGuid().ToString();
    public string ProfileName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Default Profile" : value.Trim(); } = "Default Profile";
    /// <summary>
    /// The profile's pages. The non-empty invariant is enforced at this
    /// boundary — a null or empty assignment repairs to a single default
    /// page (the same repair rule the <see cref="PageName"/> and
    /// <see cref="BackgroundHexColor"/> setters apply) — so
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
}
