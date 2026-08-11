> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`). Archived for history.

# Page Tab Scroll Navigation Specification

## Overview
When many pages are added to the ModernWigiDash dashboard, the page tab bar overflows the header width with no way to scroll between the first and last page. This specification implements horizontal drag/scroll navigation for the page tabs, allowing users to scroll through tabs with the mouse wheel or drag when they overflow.

## Goals & Objectives
1. **Horizontal scroll navigation** — wrap page tabs in a scrollable container so users can navigate between tabs when they overflow the header width.
2. **Mouse wheel support** — convert vertical mouse wheel events to horizontal scroll when hovering over the tab bar.
3. **Auto-scroll to active tab** — when switching pages or adding new pages, automatically scroll to make the active tab visible.
4. **Clean aesthetic** — no extra UI elements (arrows, buttons); the scroll behavior is implicit and discoverable via mouse wheel.

## Architectural & Component Changes

### 1. XAML: ScrollViewer Wrapper (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (lines 47-50)

Replace the current `PanelPageTabs` StackPanel with a ScrollViewer wrapping the StackPanel:

**Before:**
```xml
<StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
```

**After:**
```xml
<ScrollViewer x:Name="ScrollerPageTabs" HorizontalScrollBarVisibility="Auto" 
              VerticalScrollBarVisibility="Disabled" 
              MouseWheel="ScrollerPageTabs_MouseWheel">
    <StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
</ScrollViewer>
```

- `HorizontalScrollBarVisibility="Auto"` — scrollbar appears only when tabs overflow.
- `VerticalScrollBarVisibility="Disabled"` — prevents vertical scrolling.
- `MouseWheel` event handler converts vertical wheel to horizontal scroll.

### 2. Code-behind: Mouse Wheel Handler (`MainWindow.xaml.cs`)
Location: `ModernWigiDash.App/MainWindow.xaml.cs` (near other event handlers)

Add a new event handler:
```csharp
private void ScrollerPageTabs_MouseWheel(object sender, MouseWheelEventArgs e)
{
    ScrollerPageTabs.ScrollToHorizontalOffset(
        ScrollerPageTabs.HorizontalOffset - e.Delta);
}
```

This converts vertical mouse wheel delta to horizontal scroll offset. Negative delta (scroll up) scrolls left; positive delta (scroll down) scrolls right.

### 3. Code-behind: Auto-scroll to Active Tab (`MainWindow.xaml.cs`)
Location: `ModernWigiDash.App/MainWindow.xaml.cs`, `RebuildPageTabsUI()` method (line 1762)

After building the tab buttons and setting the active tab, add auto-scroll logic:
```csharp
// At the end of RebuildPageTabsUI(), after the for loop:
if (PanelPageTabs.Children.Count > _profile.ActivePageIndex && 
    PanelPageTabs.Children[_profile.ActivePageIndex] is FrameworkElement activeTab)
{
    activeTab.BringIntoView();
}
```

Also add the same logic in `SwitchToPage()` (line 1887) after updating `_profile.ActivePageIndex`:
```csharp
if (PanelPageTabs.Children.Count > index && 
    PanelPageTabs.Children[index] is FrameworkElement targetTab)
{
    targetTab.BringIntoView();
}
```

### 4. Explicitly Out of Scope
- No changes to tab visual styling (buttons remain as-is).
- No changes to rename/close button functionality.
- No changes to the "+ Add Page" button.
- No horizontal scrollbar thumb styling (default WPF scrollbar is acceptable).

## Verification Plan
1. **Build**: `dotnet build ModernWigiDash.slnx` — 0 errors.
2. **Manual verification**:
   - Add 10+ pages — tabs should overflow and become scrollable.
   - Mouse wheel over tab bar — tabs scroll horizontally.
   - Click a hidden tab — it scrolls into view.
   - Switch between pages — active tab auto-scrolls into view.
   - Remove pages until tabs fit — scrollbar disappears.
