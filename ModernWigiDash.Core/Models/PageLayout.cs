namespace ModernWigiDash.Core.Models;

public class PageLayout
{
    public string PageId { get; set; } = Guid.NewGuid().ToString();
    public string PageName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Main Dashboard" : value.Trim(); } = "Main Dashboard";
    public string BackgroundHexColor { get; set => field = string.IsNullOrWhiteSpace(value) ? "#12141D" : value.Trim(); } = "#12141D";
    public string BackgroundImagePath { get; set; } = string.Empty;

    public bool SnapToGrid { get; set; } = true;

    public List<PlacedWidgetInstance> Widgets { get; set; } = [];
}

public class ProfileLayout
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString();
    public string ProfileName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Default Profile" : value.Trim(); } = "Default Profile";
    public List<PageLayout> Pages { get; set; } = [new PageLayout()];

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

    /// <summary>The active page. WARNING: the empty-Pages fallback hands out a
    /// freshly constructed orphan page that is NOT part of <see cref="Pages"/>
    /// — mutations on it (widget placement, renaming) are lost. It is
    /// unreachable in practice (the ctor creates one page, ProfileOps refuses
    /// to delete the last, the import sanitizer repairs empty pages) and is
    /// kept only as pure defense against a hand-constructed empty profile;
    /// any code that relies on it is a bug.</summary>
    public PageLayout ActivePage => Pages.Count > 0 && ActivePageIndex >= 0 && ActivePageIndex < Pages.Count
        ? Pages[ActivePageIndex]
        : Pages.FirstOrDefault() ?? new PageLayout();
}
