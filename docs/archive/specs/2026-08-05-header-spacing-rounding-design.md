# Header Layout Spacing & Rounding Specification

## Overview
Improve the header layout by adding more space between the logo, page tabs, "+ Add Page" button, and "Snap to Grid" checkbox. Round the edges of the ScrollViewer (page tab container) for a softer look.

## Goals & Objectives
1. **Increase spacing** between the logo/subtitle and the first page tab.
2. **Increase spacing** between the last page tab and the "+ Add Page" button.
3. **Add visual separation** between "+ Add Page" and "Snap to Grid" with a vertical divider.
4. **Round the ScrollViewer edges** (CornerRadius=12) with a subtle background and border.

## Architectural & Component Changes

### 1. Logo → Tabs Spacing (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (line 36, StackPanel with logo)

Change the logo StackPanel margin from `Margin="0,0,12,0"` to `Margin="0,0,24,0"`.

### 2. ScrollViewer → + Add Page Spacing (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (BtnAddPage, line 53)

Change `Margin="6,0,0,0"` to `Margin="12,0,0,0"`.

### 3. Vertical Divider (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (between BtnAddPage and ChkSnapToGrid)

Add a vertical divider Border between the "+ Add Page" button and the Snap to Grid checkbox:
```xml
<Border Grid.Column="2" Width="1" Height="24" Background="{DynamicResource BorderBrush}" Margin="12,0" VerticalAlignment="Center"/>
```
This requires shifting ChkSnapToGrid and subsequent elements to the next Grid column, or inserting the divider as a sibling in the same StackPanel.

### 4. Rounded ScrollViewer (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (ScrollViewer, line 48)

Wrap the `ScrollViewer` in a `Border` with:
- `Background="{DynamicResource BgCard}"`
- `BorderBrush="{DynamicResource BorderBrush}"`
- `BorderThickness="1"`
- `CornerRadius="12"`
- `Padding="4,4"`

### 5. Explicitly Out of Scope
- No changes to `MainWindow.xaml.cs`.
- No changes to button styling, tab content, or scroll behavior.
- No changes to the "+ Add Page" button text or icon.

## Verification Plan
1. **Build**: `dotnet build ModernWigiDash.slnx` — 0 errors.
2. **Manual verification**: Launch app, confirm:
   - More space between logo subtitle and first tab.
   - More space between last tab and "+ Add Page".
   - Vertical divider between "+ Add Page" and "Snap to Grid".
   - ScrollViewer has rounded edges with subtle background.
