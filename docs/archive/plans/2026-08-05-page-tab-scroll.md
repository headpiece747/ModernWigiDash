> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`): `PageTabsView` scroll module (wheel inversion + scroll-into-view). Archived for history.

# Page Tab Scroll Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add horizontal scroll navigation to the page tab bar so users can scroll through tabs when they overflow the header width.

**Architecture:** Wrap the existing `PanelPageTabs` StackPanel in a WPF `ScrollViewer` with horizontal scroll enabled. Convert vertical mouse wheel events to horizontal scroll. Auto-scroll to the active tab when switching pages.

**Tech Stack:** WPF (.NET 10, `net10.0-windows10.0.19041.0`), C#, XAML.

## Global Constraints

- Target framework `net10.0-windows10.0.19041.0`.
- No new NuGet packages or project references.
- Follow existing code patterns in `MainWindow.xaml` and `MainWindow.xaml.cs`.
- Build must succeed with 0 errors (pre-existing warnings acceptable).

## File Structure

- Modify: `ModernWigiDash.App/MainWindow.xaml:47-50` — wrap `PanelPageTabs` in `ScrollViewer`.
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1762` — add auto-scroll logic in `RebuildPageTabsUI()`.
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1887` — add auto-scroll logic in `SwitchToPage()`.
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` — add `ScrollerPageTabs_MouseWheel` event handler.

---

### Task 1: Add ScrollViewer wrapper and mouse wheel handler

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml:47-50`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` (new method)

**Interfaces:**
- Consumes: existing `PanelPageTabs` StackPanel (line 48).
- Produces: `ScrollerPageTabs` ScrollViewer (referenced by auto-scroll logic in Task 2).

- [ ] **Step 1: Wrap PanelPageTabs in ScrollViewer**

In `MainWindow.xaml`, replace the `PanelPageTabs` line (line 48) with:

```xml
                <ScrollViewer x:Name="ScrollerPageTabs" HorizontalScrollBarVisibility="Auto" 
                              VerticalScrollBarVisibility="Disabled" 
                              MouseWheel="ScrollerPageTabs_MouseWheel">
                    <StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
                </ScrollViewer>
```

- [ ] **Step 2: Add MouseWheel event handler**

In `MainWindow.xaml.cs`, add this method near the other event handlers (e.g., near `TxtSearchCatalog_TextChanged` around line 1724):

```csharp
    private void ScrollerPageTabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollerPageTabs.ScrollToHorizontalOffset(
            ScrollerPageTabs.HorizontalOffset - e.Delta);
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(app): add ScrollViewer wrapper for page tabs"
```

---

### Task 2: Add auto-scroll to active tab

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1762` (`RebuildPageTabsUI` method)
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1887` (`SwitchToPage` method)

**Interfaces:**
- Consumes: `ScrollerPageTabs` ScrollViewer (Task 1), `PanelPageTabs` children.
- Produces: active tab is scrolled into view after rebuild or page switch.

- [ ] **Step 1: Add auto-scroll in RebuildPageTabsUI**

At the end of `RebuildPageTabsUI()` (after the `for` loop, around line 1825), add:

```csharp
        // Auto-scroll to active tab
        if (PanelPageTabs.Children.Count > _profile.ActivePageIndex &&
            PanelPageTabs.Children[_profile.ActivePageIndex] is FrameworkElement activeTab)
        {
            activeTab.BringIntoView();
        }
```

- [ ] **Step 2: Add auto-scroll in SwitchToPage**

In `SwitchToPage()` (around line 1887), after `RebuildPageTabsUI()` is called (or after `_profile.ActivePageIndex = index;`), add:

```csharp
        // Auto-scroll to the newly active tab
        if (PanelPageTabs.Children.Count > index &&
            PanelPageTabs.Children[index] is FrameworkElement targetTab)
        {
            targetTab.BringIntoView();
        }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 4: Manual verification**

Launch the app, add 10+ pages, verify:
- Tabs overflow and become scrollable.
- Mouse wheel scrolls horizontally.
- Clicking a hidden tab scrolls it into view.
- Switching pages auto-scrolls to the active tab.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(app): auto-scroll to active page tab"
```
