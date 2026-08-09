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
    public int ActivePageIndex { get; set => field = Math.Max(0, value); } = 0;

    public PageLayout ActivePage => Pages.Count > 0 && ActivePageIndex >= 0 && ActivePageIndex < Pages.Count
        ? Pages[ActivePageIndex]
        : Pages.FirstOrDefault() ?? new PageLayout();
}
