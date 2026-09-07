namespace ModernWigiDash.Sdk;

/// <summary>
/// The page-navigation facet of the widget host context: the member a widget
/// needs to flip the profile's active page (the hotkey widget's page-flip
/// actions). Split from <see cref="IModernWigiDashContext"/> so a widget that
/// navigates pages depends on this capability explicitly instead of seeing
/// every host service (the <c>IWidgetActionInvoker</c> /
/// <c>IWidgetEditorProvider</c> optional-facet precedent). A host that does not
/// track pages simply does not implement it;
/// <see cref="ModernWidgetBase.NavigationContext"/> is then null and the
/// navigation degrades to a no-op.
/// </summary>
public interface IWidgetNavigationContext
{
    /// <summary>
    /// Navigates the profile's active page by the given delta (positive =
    /// forward, negative = back). The page boundary clamps identically to a
    /// swipe (the host's SetActivePageIndex gate); a zero or out-of-range step
    /// is a no-op. The App's context routes it to its SwitchToPage seam.
    /// </summary>
    void NavigatePage(int delta);
}
