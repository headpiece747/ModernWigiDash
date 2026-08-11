# Window Cleanup + Page Tabs Under Canvas + Keyboard Delete — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clean up the app window's marketing text, move page tabs from the top header to under the preview canvas, and let Delete/Back remove the selected widget.

**Architecture:** Pure `ModernWigiDash.App` WPF chrome change. XAML layout edits in `MainWindow.xaml` (text removals, tab-strip relocation, `PreviewKeyDown` wiring) plus one extracted method + one key handler in `MainWindow.xaml.cs`. No Core/Widgets/Service changes.

**Tech Stack:** WPF (.NET 10), C# 14, file-scoped namespaces, code-behind handlers (existing project pattern).

## Global Constraints

- Element names `ScrollerPageTabs`, `PanelPageTabs`, `BtnAddPage` must be preserved (code-behind `BtnAddPage_Click`, `ScrollerPageTabs_MouseWheel`, page-refresh code depends on them).
- Status bar must keep `TxtActiveCount` ("Active Widgets: 0"); only `TxtStatusTelemetry` is deleted.
- Delete/Back must never fire while an inspector `TextBox` has focus.
- Verify with the temp-output test command when the service is running: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`.

---

### Task 1: XAML layout changes (text removals + tab strip under canvas)

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml`

**Interfaces:**
- Consumes: nothing (existing XAML structure).
- Produces: relocated `ScrollerPageTabs`/`PanelPageTabs`/`BtnAddPage` (same names, new location) — consumed by Task 2's code-behind only via existing handlers; no signature changes.

- [ ] **Step 1: Apply the four text removals**

Edit `MainWindow.xaml`:

1. Line 8: `Title="ModernWigiDash - Next-Generation .NET 10"` → `Title="ModernWigiDash"`
2. Line 42: `Text="Free-Form Canvas &amp; Dynamic Engine"` → `Text="Free-Form Canvas"`
3. Line 137: `Text="🖥️ LIVE WIGIDASH PREVIEW (1016 x 592 • 30 FPS)"` → `Text="🖥️ LIVE WIGIDASH PREVIEW (1016 x 592)"`
4. Line 262: delete the entire line `<TextBlock x:Name="TxtStatusTelemetry" Text=".NET 10 | Rendering: 30 FPS | Canvas: 1016 x 592" FontSize="11" Foreground="White" Margin="0,0,16,0"/>` — leave `TxtActiveCount` and the rest of the status bar untouched.

- [ ] **Step 2: Remove the header's center page-tab section**

Delete the entire center Grid (lines 47–60):

```xml
                <!-- Center Page Tabs -->
                <Grid Grid.Column="1" HorizontalAlignment="Center" VerticalAlignment="Center">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <Border Grid.Column="0" CornerRadius="12" Padding="4,4" VerticalAlignment="Center">
                        <ScrollViewer x:Name="ScrollerPageTabs" HorizontalScrollBarVisibility="Auto"
                                      VerticalScrollBarVisibility="Disabled" MaxWidth="800"
                                      MouseWheel="ScrollerPageTabs_MouseWheel">
                            <StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
                        </ScrollViewer>
                    </Border>
                    <Button x:Name="BtnAddPage" Grid.Column="1" Content="+ Add Page" Margin="12,0,0,0" Padding="12,6" FontSize="12" VerticalAlignment="Center" Click="BtnAddPage_Click"/>
                </Grid>
```

Keep the header Grid's three column definitions (the middle `*` column becomes an empty spacer).

- [ ] **Step 3: Add the third row + tab strip under the canvas**

In the center column Grid (`Grid.Column="1"`, currently two row definitions — `36` header + `*` canvas):

1. Add a third row definition:

```xml
                    <Grid.RowDefinitions>
                        <RowDefinition Height="36"/>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
```

2. After the closing `</Border>` of `PreviewFrame` (line 153, still inside the center column Grid), insert the tab strip:

```xml
                <!-- Page tabs: relocated from the top header to under the canvas -->
                <Border Grid.Row="2" Background="{DynamicResource BgPanel}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0" Padding="12,8">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <ScrollViewer x:Name="ScrollerPageTabs" Grid.Column="0" HorizontalScrollBarVisibility="Auto"
                                      VerticalScrollBarVisibility="Disabled" MaxWidth="900"
                                      HorizontalAlignment="Center"
                                      MouseWheel="ScrollerPageTabs_MouseWheel">
                            <StackPanel x:Name="PanelPageTabs" Orientation="Horizontal"/>
                        </ScrollViewer>
                        <Button x:Name="BtnAddPage" Grid.Column="1" Content="+ Add Page" Margin="12,0,0,0" Padding="12,6" FontSize="12" VerticalAlignment="Center" Click="BtnAddPage_Click"/>
                    </Grid>
                </Border>
```

- [ ] **Step 4: Build to verify XAML compiles**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo`
Expected: `Build succeeded. 0 Error(s)`. XAML is compiled at build time — errors here would fail the build.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml
git commit -m "feat(ui): remove marketing text, move page tabs under preview canvas"
```

---

### Task 2: Keyboard Delete/Back removes the selected widget

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml` (root Window tag — add `PreviewKeyDown` attribute)
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:760` (`BtnDeleteWidget_Click` region)

**Interfaces:**
- Consumes: `_selectedWidget`, `_profile.ActivePage.Widgets`, `SelectWidget`, `UpdateActiveCount`, `SkiaCanvas` (all existing members).
- Produces: `DeleteSelectedWidget()` (private, parameterless) and `MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)`.

- [ ] **Step 1: Extract the shared delete method**

Replace the body of `BtnDeleteWidget_Click` (`MainWindow.xaml.cs:760-769`) with a call to a new method, and add the method plus the key handler below it:

```csharp
    private void BtnDeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedWidget();
    }

    /// <summary>Single delete path shared by the inspector button and the Delete/Back key.</summary>
    private void DeleteSelectedWidget()
    {
        if (_selectedWidget != null)
        {
            _profile.ActivePage.Widgets.Remove(_selectedWidget);
            SelectWidget(null);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    /// <summary>
    /// Delete/Back removes the selected widget — except while typing in an
    /// inspector text box, where Backspace must edit the field, not delete.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Delete || e.Key == Key.Back) &&
            Keyboard.FocusedElement is not TextBox)
        {
            DeleteSelectedWidget();
            e.Handled = true;
        }
    }
```

Required usings are already present in `MainWindow.xaml.cs` (header): `System.Windows.Input` (Key, Keyboard, KeyEventArgs) and `System.Windows.Controls` (TextBox).

- [ ] **Step 2: Wire the handler on the root Window**

In `MainWindow.xaml`, add the `PreviewKeyDown` attribute to the root `<Window ...>` element (with the other attributes, e.g. after `Title`):

```xml
        PreviewKeyDown="MainWindow_PreviewKeyDown"
```

- [ ] **Step 3: Build + run the full test suite**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo`
Expected: `Build succeeded. 0 Error(s)`

Run: `dotnet test ModernWigiDash.slnx -c Release --no-build --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\`
Expected: `Passed! - Failed: 0` (295 tests)

- [ ] **Step 4: Live verification (manual)**

Restart the service + app (elevated):
1. Window title reads `ModernWigiDash`; header subtitle reads `Free-Form Canvas`; preview header reads `LIVE WIGIDASH PREVIEW (1016 x 592)`; status bar has no `.NET 10 | Rendering: 30 FPS` text.
2. Page tabs + `+ Add Page` render under the canvas; clicking tabs still switches pages; `+ Add Page` still adds a page.
3. Select a widget on the canvas → press Delete → widget removed, count decremented.
4. Focus an inspector text box (e.g. X Position) → press Backspace → character deleted, widget NOT removed.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(ui): delete/back key removes selected widget via shared delete path"
```
