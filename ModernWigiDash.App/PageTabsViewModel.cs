using ModernWigiDash.Core.Models;

namespace ModernWigiDash.App;

/// <summary>The pure description of one page tab — the rules RebuildPageTabsUI
/// applies, testable without WPF: which tab is active, whether deletion is
/// allowed (never on the last page), and the tab label.</summary>
public sealed record PageTabItem(string PageName, int Index, bool IsActive, bool CanDelete);

/// <summary>
/// Builds the per-tab rule set for the page-tabs strip. The window renders
/// these items; the rules (active index, delete-only-when-more-than-one-page)
/// live here so they can be tested directly.
/// </summary>
public static class PageTabsViewModel
{
    /// <summary>The single delete-page rule: the last page can never be
    /// deleted. Shared by the tab strip and the window's delete flow — the
    /// rule is derived in exactly one place.</summary>
    public static bool CanDelete(ProfileLayout profile) => profile.Pages.Count > 1;

    public static IReadOnlyList<PageTabItem> Build(ProfileLayout profile)
    {
        bool canDelete = CanDelete(profile);
        var items = new PageTabItem[profile.Pages.Count];
        for (int i = 0; i < profile.Pages.Count; i++)
        {
            items[i] = new PageTabItem(profile.Pages[i].PageName, i, i == profile.ActivePageIndex, canDelete);
        }
        return items;
    }
}
