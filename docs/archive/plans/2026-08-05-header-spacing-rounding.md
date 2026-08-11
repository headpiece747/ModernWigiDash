> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`): header spacing + ScrollViewer rounding in `MainWindow.xaml`. Archived for history.

# Header Layout Spacing & Rounding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve header spacing between logo, tabs, "+ Add Page", and "Snap to Grid", and round the ScrollViewer edges.

**Architecture:** XAML-only changes in `MainWindow.xaml`. No code-behind modifications. All spacing and rounding applied via margin, padding, CornerRadius, and a vertical divider element.

**Tech Stack:** WPF (.NET 10, `net10.0-windows10.0.19041.0`), XAML.

## Global Constraints

- Target framework `net10.0-windows10.0.19041.0`.
- No new NuGet packages or project references.
- Only `MainWindow.xaml` is modified — no code-behind changes.
- Build must succeed with 0 errors.

## File Structure

- Modify: `ModernWigiDash.App/MainWindow.xaml` — logo margin, ScrollViewer rounding, button margin, vertical divider.

---

### Task 1: Apply spacing and rounding changes

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml` (multiple locations)

**Interfaces:**
- Consumes: existing XAML elements (logo StackPanel, ScrollViewer, BtnAddPage, ChkSnapToGrid).
- Produces: updated header layout with improved spacing and rounded ScrollViewer.

- [ ] **Step 1: Increase logo → tabs spacing**

Find the logo StackPanel (line 36, containing the `Image` and text `StackPanel`). Change its `Margin` from `"0,0,12,0"` to `"0,0,24,0"`.

- [ ] **Step 2: Round the ScrollViewer**

Wrap the `ScrollViewer` in a `Border`. The current XAML (lines 48-52):
```xml
                    <ScrollViewer x:Name="ScrollerPageTabs" Grid.Column="0" HorizontalScrollBarVisibility="Auto" 
                                  VerticalScrollBarVisibility="Disabled" MaxWidth="800"
                                  MouseWheel="ScrollerPageTabs_MouseWheel">
                        <StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
                    </ScrollViewer>
```

Replace with:
```xml
                    <Border Grid.Column="0" Background="{DynamicResource BgCard}" 
                            BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" 
                            CornerRadius="12" Padding="4,4" VerticalAlignment="Center">
                        <ScrollViewer x:Name="ScrollerPageTabs" HorizontalScrollBarVisibility="Auto" 
                                      VerticalScrollBarVisibility="Disabled" MaxWidth="800"
                                      MouseWheel="ScrollerPageTabs_MouseWheel">
                            <StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
                        </ScrollViewer>
                    </Border>
```

- [ ] **Step 3: Increase + Add Page spacing**

Find `BtnAddPage` (line 53). Change `Margin="6,0,0,0"` to `Margin="12,0,0,0"`.

- [ ] **Step 4: Add vertical divider**

Insert a vertical divider `Border` between `BtnAddPage` and `ChkSnapToGrid`. The current center column Grid has two columns (ScrollViewer + Button). The right side is a separate `StackPanel` (Grid.Column="2"). The divider goes between the center and right columns, or as the first element in the right StackPanel.

Add as the first child in the right-side StackPanel (before `ChkSnapToGrid`):
```xml
                    <Border Width="1" Height="24" Background="{DynamicResource BorderBrush}" Margin="0,0,12,0" VerticalAlignment="Center"/>
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml
git commit -m "feat(app): improve header spacing and round ScrollViewer edges"
```
