using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModernWigiDash.Core.Models;

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
    /// active tab into view.</summary>
    public void Rebuild(ProfileLayout profile)
    {
        _panel.Children.Clear();
        foreach (var tab in PageTabsViewModel.Build(profile))
        {
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

    /// <summary>One tab: the page button (accent when active) plus the rename
    /// icon button, and the close icon button when deletion is allowed.</summary>
    private Grid BuildTabContainer(PageTabItem tab, PageTabVisual visual)
    {
        var container = new Grid { Margin = new Thickness(3, 0, 3, 0) };

        var pageButton = new Button
        {
            Content = $"📄 {tab.PageName}",
            Padding = visual.TabPadding,
            Style = visual.IsActive ? (Style)_findResource("AccentButton")! : (Style)_findResource(typeof(Button))!,
        };
        pageButton.Click += (_, _) => _switchToPage(tab.Index);
        container.Children.Add(pageButton);

        container.Children.Add(BuildIconButton(
            content: "✏️",
            toolTip: "Rename page",
            margin: visual.RenameIconMargin,
            isActive: visual.IsActive,
            onClick: (_, _) => _renamePage(tab.Index)));

        if (visual.CanDelete)
        {
            container.Children.Add(BuildIconButton(
                content: "✕",
                toolTip: null,
                margin: visual.CloseIconMargin,
                isActive: visual.IsActive,
                onClick: (_, _) => _deletePage(tab.Index)));
        }

        return container;
    }

    /// <summary>The one icon-button builder shared by the rename and close
    /// buttons: identical 20×20 right-aligned geometry, differing only in
    /// content, tooltip, margin, and click action.</summary>
    private Button BuildIconButton(
        string content,
        string? toolTip,
        Thickness margin,
        bool isActive,
        RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = content,
            FontSize = PageTabVisual.IconFontSize,
            ToolTip = toolTip,
            Foreground = isActive ? Brushes.White : (Brush)_findResource("TextSecondary")!,
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
        button.Click += onClick;
        return button;
    }
}
