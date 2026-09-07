using System.Windows.Automation;
using System.Windows.Input;

namespace ModernWigiDash.App;

/// <summary>
/// The page-tabs strip module: owns the tab construction (one tab button plus
/// the rename/close icon buttons per tab), the horizontal-scroll wheel
/// behavior, and the scroll-into-view navigation. The window keeps only the
/// switch/rename/delete page seams and the XAML surfaces; the geometry rules
/// live in <see cref="PageTabVisual"/>. The wheel handler subscribes here, so
/// the window owns no tab-strip event code at all.
/// </summary>
internal sealed class PageTabsView
{
    private readonly Panel _panel;
    private readonly ScrollViewer _scrollViewer;
    private readonly Func<object, object?> _findResource;
    private readonly Action<int> _switchToPage;
    private readonly Action<int> _renamePage;
    private readonly Action<int> _deletePage;

    /// <param name="panel">The tab strip panel (PanelPageTabs).</param>
    /// <param name="scrollViewer">The strip's horizontal scroller
    /// (ScrollerPageTabs) — the wheel handler is attached here.</param>
    /// <param name="findResource">Resource lookup for the accent/plain button
    /// styles and the secondary-text brush.</param>
    /// <param name="switchToPage">Page-switch seam (activates the tab).</param>
    /// <param name="renamePage">Rename seam (prompts for the new name).</param>
    /// <param name="deletePage">Delete seam (confirms + removes the page).</param>
    public PageTabsView(
        Panel panel,
        ScrollViewer scrollViewer,
        Func<object, object?> findResource,
        Action<int> switchToPage,
        Action<int> renamePage,
        Action<int> deletePage)
    {
        _panel = panel;
        _scrollViewer = scrollViewer;
        _findResource = findResource;
        _switchToPage = switchToPage;
        _renamePage = renamePage;
        _deletePage = deletePage;
        _scrollViewer.MouseWheel += OnScrollViewerMouseWheel;
    }

    /// <summary>Rebuilds the whole tab strip from the profile and brings the
    /// active tab into view. The per-tab facts (active index, delete-only-when-
    /// more-than-one-page via <see cref="ProfileOps.CanDeletePage"/>) are derived
    /// here so the tab strip and the delete operation can never drift.</summary>
    public void Rebuild(ProfileLayout profile)
    {
        _panel.Children.Clear();
        bool canDelete = ProfileOps.CanDeletePage(profile);
        for (int i = 0; i < profile.Pages.Count; i++)
        {
            var tab = new PageTabItem(profile.Pages[i].PageName, i, i == profile.ActivePageIndex, canDelete);
            _panel.Children.Add(BuildTabContainer(tab, new PageTabVisual(tab)));
        }

        ScrollToPage(profile.ActivePageIndex);
    }

    /// <summary>Brings the page tab at the given index into view.</summary>
    public void ScrollToPage(int index)
    {
        if (_panel.Children.Count > index &&
            _panel.Children[index] is FrameworkElement targetTab)
        {
            targetTab.BringIntoView();
        }
    }

    /// <summary>The strip scrolls horizontally with the wheel like a
    /// horizontal scroller (the wheel's vertical delta maps to horizontal
    /// offset, inverted to match the scroll direction).</summary>
    private void OnScrollViewerMouseWheel(object _, MouseWheelEventArgs e)
    {
        _scrollViewer.ScrollToHorizontalOffset(_scrollViewer.HorizontalOffset - e.Delta);
    }

    private static readonly Geometry FallbackEdit =
        Geometry.Parse("M20.975 3.025A3.48 3.48 0 0 0 18.5 2a3.48 3.48 0 0 0-2.475 1.025l-13.17 13.17-.845 4.93a.74.74 0 0 0 .21.655c.14.14.335.22.53.22.04 0 .085 0 .125-.01l4.93-.845 13.17-13.17A3.48 3.48 0 0 0 22 5.5a3.48 3.48 0 0 0-1.025-2.475Zm-13.89 16.72-3.415.585.585-3.415 9.3-9.3 2.83 2.83-9.3 9.3Zm10.36-10.36-2.83-2.83 2.47-2.47c.755-.755 2.07-.755 2.83 0a2.002 2.002 0 0 1 0 2.83l-2.47 2.47Z");

    private static readonly Geometry FallbackClose =
        Geometry.Parse("M18.3 5.71a.996.996 0 0 0-1.41 0L12 10.59 7.11 5.7A.996.996 0 1 0 5.7 7.11L10.59 12 5.7 16.89a.996.996 0 1 0 1.41 1.41L12 13.41l4.89 4.89a.996.996 0 1 0 1.41-1.41L13.41 12l4.89-4.89c.38-.38.38-1.02 0-1.4Z");

    /// <summary>One tab: the page button (accent when active) plus the rename
    /// icon button, and the close icon button when deletion is allowed.</summary>
    private Grid BuildTabContainer(PageTabItem tab, PageTabVisual visual)
    {
        var container = new Grid { Margin = new Thickness(3, 0, 3, 0) };

        var pageButton = new Button
        {
            Content = tab.PageName,
            FontSize = PageTabVisual.TabFontSize,
            Padding = visual.TabPadding,
            Style = visual.IsActive ? (Style)_findResource("AccentButton")! : (Style)_findResource(typeof(Button))!,
        };
        AutomationProperties.SetAutomationId(pageButton, $"PageTab_{tab.Index}");
        pageButton.Click += (_, _) => _switchToPage(tab.Index);
        container.Children.Add(pageButton);

        container.Children.Add(BuildIconButton(
            geometryKey: "IconEdit",
            toolTip: "Rename page",
            margin: visual.RenameIconMargin,
            isActive: visual.IsActive,
            automationId: $"PageTabRename_{tab.Index}",
            onClick: (_, _) => _renamePage(tab.Index)));

        if (visual.CanDelete)
        {
            container.Children.Add(BuildIconButton(
                geometryKey: "IconClose",
                toolTip: "Delete page",
                margin: visual.CloseIconMargin,
                isActive: visual.IsActive,
                automationId: $"PageTabDelete_{tab.Index}",
                onClick: (_, _) => _deletePage(tab.Index)));
        }

        return container;
    }

    /// <summary>The one icon-button builder shared by the rename and close
    /// buttons: identical 20×20 right-aligned geometry, differing only in
    /// geometry, tooltip, margin, and click action.</summary>
    private Button BuildIconButton(
        string geometryKey,
        string? toolTip,
        Thickness margin,
        bool isActive,
        string? automationId = null,
        RoutedEventHandler onClick = null!)
    {
        Geometry geom = (_findResource(geometryKey) as Geometry)
            ?? (string.Equals(geometryKey, "IconClose", StringComparison.Ordinal) ? FallbackClose : FallbackEdit);

        var path = new System.Windows.Shapes.Path
        {
            Data = geom,
            Width = 9,
            Height = 9,
            Stretch = Stretch.Uniform,
            Fill = isActive ? Brushes.White : ((Brush?)_findResource("TextSecondary") ?? Brushes.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Button
        {
            Content = path,
            ToolTip = toolTip,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = PageTabVisual.IconSize,
            Height = PageTabVisual.IconSize,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
            Cursor = Cursors.Hand,
        };
        if (automationId is not null)
            AutomationProperties.SetAutomationId(button, automationId);
        button.Click += onClick;
        return button;
    }
}
