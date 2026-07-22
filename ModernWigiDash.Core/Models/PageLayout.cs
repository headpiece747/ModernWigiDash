namespace ModernWigiDash.Core.Models;

public class PageLayout
{
    public string PageId { get; set; } = Guid.NewGuid().ToString();
    public string PageName { get; set; } = "Main Dashboard";
    public string BackgroundHexColor { get; set; } = "#12141D";
    public string BackgroundImagePath { get; set; } = string.Empty;

    public bool SnapToGrid { get; set; } = true;
    public float GridSpacingPx { get; set; } = 25f;

    public List<PlacedWidgetInstance> Widgets { get; set; } = new();
}

public class ProfileLayout
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString();
    public string ProfileName { get; set; } = "Default Profile";
    public List<PageLayout> Pages { get; set; } = new() { new PageLayout() };
    public int ActivePageIndex { get; set; } = 0;

    public PageLayout ActivePage => Pages.Count > 0 && ActivePageIndex >= 0 && ActivePageIndex < Pages.Count
        ? Pages[ActivePageIndex]
        : Pages.FirstOrDefault() ?? new PageLayout();
}
