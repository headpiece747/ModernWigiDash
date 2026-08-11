> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`): single-action `HotkeyButtonWidget` (ActionType/Icon/IconFile + media keys) and the icon-name box + Browse popup selector. The session-specific "never stage or commit" working-tree instructions were trimmed on archival. Archived for history.

# Hotkey Widget Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the Hotkey widget to a single-action button (Launch App / Open URL / one of 7 media keys, Task Manager removed) and replace its icon picker with an icon-name box + Browse... popup that supports both the bundled Griddy icons and copied single-path SVG files.

**Architecture:** Flatten the action model onto the existing scalar `ActionType`/`ActionCommand` properties (Approach 1) so profile JSON with the old list-based properties still loads. Keep `Icon` (Griddy name) and add `IconFile` (relative SVG path that wins at render). Custom SVGs are parsed once with `SKPath.ParseSvgPathData` from the first (and only) `<path>` `d` attribute, cached like `GriddyIcons.PathCache`, tinted by `IconColorHex`. The inspector's Icon editor becomes a box + Browse... button that opens a popup selector; `ShowHotkeyActionEditor` and the toggle/action-list machinery are deleted.

**Tech Stack:** .NET 10 / C# / WPF (`MainWindow.xaml.cs`), SkiaSharp (`SKPath.ParseSvgPathData`), MSTest 4.3.2, `System.Xml.Linq` (in-box). No new NuGet packages.

## Global Constraints

- Run tests from the repo root with `dotnet test ModernWigiDash.slnx`; task-level verification uses `--filter` so unrelated failures stay out of the way.
- `ModernWigiDash.Widgets` has `[assembly: InternalsVisibleTo("ModernWigiDash.Tests")]` — internal helpers are directly testable.
- `WidgetPropertyType.Icon` is used **only** by `HotkeyButtonWidget` — the Icon editor branch can be rewritten unconditionally.
- `ActionType` values (exact strings): `"Launch App"`, `"Open URL"`, `"Media Play / Pause"`, `"Media Next"`, `"Media Previous"`, `"Media Stop"`, `"Volume Up"`, `"Volume Down"`, `"Mute"`. Default: `"Launch App"`.
- Media key VK codes already fixed in `HotkeyActionExecutor.ParseVirtualKey` (`UtilityAndInteractiveWidgets.cs:237`): PLAYPAUSE `0xB3`, NEXT `0xB0`, PREVIOUS `0xB1`, STOP `0xB2`, VOLUMEUP `0xAF`, VOLUMEDOWN `0xAE`, MUTE `0xAD`. `MediaKeyCatalog.Options` lists these 7.
- Custom SVGs are stored in `%LocalAppData%\ModernWigiDash\icons\` (consistent with `TwitchTokenStore.cs:12`). `IconFile` stores only the **bare file name** relative to that folder (e.g. `power-logo.svg`) — `SvgIconLoader.ResolveFullPath` combines it with the icons directory.
- Only single-path SVG files are supported. Multiple `<path>` elements, no `<path>`, or a missing file → log via `Context?.LogError` (null-safe; tests construct widgets without a Context) and render label-only. **No** silent fallback to a Griddy `Icon` when `IconFile` is set.
- When both `IconFile` and `Icon` are set, `IconFile` wins.
- Remove `ToggleActions`, `ToggledButtonLabel`, `Actions`, `ToggledActions` and all toggle runtime state; delete `CreateLegacyAction` and `ShowHotkeyActionEditor`/`CloneHotkeyAction`.
- `HotkeyAction` and `HotkeyActionExecutor` classes stay unchanged (the executor is still used; tests cover `ParseVirtualKey` and `HotkeyAction.Summary`).
- Code style: no comments added; follow existing formatting in the files being edited.

---

### Task 1: Single-action model on the Hotkey widget

**Files:**
- Modify: `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs:282-458`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs:517-526` (rewrite) and new tests near `:526`

**Interfaces:**
- Consumes: existing `HotkeyActionKind`, `HotkeyAction`, `HotkeyActionExecutor`, `MediaKeyCatalog` (all unchanged).
- Produces: `internal static HotkeyAction HotkeyButtonWidget.CreateAction(string actionType, string actionCommand)` — the single mapping helper later tasks and tests call.

- [ ] **Step 1: Write the failing tests**

Replace `HotkeyWidget_DefaultActions_AreLaunchAction` (currently asserts `widget.Actions.Count`, which will not compile after the property is removed) and add the new tests. In `UnitTestSuite.cs`, replace lines 517-526 with:

```csharp
    [TestMethod]
    public void HotkeyWidget_ActionType_DefaultsToLaunchApp()
    {
        var widget = new HotkeyButtonWidget();
        Assert.AreEqual("Launch App", widget.ActionType);
        Assert.AreEqual("", widget.ActionCommand);
    }

    [TestMethod]
    public void HotkeyWidget_MediaActionTypes_MapToMediaKeys()
    {
        var map = new Dictionary<string, string>
        {
            ["Media Play / Pause"] = "PLAYPAUSE",
            ["Media Next"] = "NEXT",
            ["Media Previous"] = "PREVIOUS",
            ["Media Stop"] = "STOP",
            ["Volume Up"] = "VOLUMEUP",
            ["Volume Down"] = "VOLUMEDOWN",
            ["Mute"] = "MUTE"
        };
        foreach (var (actionType, expectedValue) in map)
        {
            var action = HotkeyButtonWidget.CreateAction(actionType, "");
            Assert.AreEqual(HotkeyActionKind.MediaKey, action.Kind, actionType);
            Assert.AreEqual(expectedValue, action.Value, actionType);
        }
    }

    [TestMethod]
    public void HotkeyWidget_TaskManagerLegacyType_MapsToLaunchTaskmgr()
    {
        var action = HotkeyButtonWidget.CreateAction("Task Manager", "");
        Assert.AreEqual(HotkeyActionKind.Launch, action.Kind);
        Assert.AreEqual("taskmgr.exe", action.Value);
    }

    [TestMethod]
    public void HotkeyWidget_OpenUrlActionType_MapsToOpenUrl()
    {
        var action = HotkeyButtonWidget.CreateAction("Open URL", "https://example.com");
        Assert.AreEqual(HotkeyActionKind.OpenUrl, action.Kind);
        Assert.AreEqual("https://example.com", action.Value);
    }

    [TestMethod]
    public void HotkeyWidget_SingleAction_ExecutesOneAction()
    {
        var launch = HotkeyButtonWidget.CreateAction("Launch App", "notepad.exe");
        Assert.AreEqual(HotkeyActionKind.Launch, launch.Kind);
        Assert.AreEqual("notepad.exe", launch.Value);
        var openUrl = HotkeyButtonWidget.CreateAction("Open URL", "https://example.com");
        Assert.AreEqual(HotkeyActionKind.OpenUrl, openUrl.Kind);
        Assert.AreEqual("https://example.com", openUrl.Value);
        var mute = HotkeyButtonWidget.CreateAction("Mute", "");
        Assert.AreEqual(HotkeyActionKind.MediaKey, mute.Kind);
        Assert.AreEqual("MUTE", mute.Value);
    }
```

The file's existing `using` directives already cover `ModernWigiDash.Widgets`, `System.Text.Json`, and `SkiaSharp`; `Dictionary` needs no extra using (`System.Collections.Generic` via implicit usings).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx --filter "FullyQualifiedName~HotkeyWidget|FullyQualifiedName~HotkeyAction|FullyQualifiedName~ParseVirtualKey|FullyQualifiedName~MediaKeyCatalog"`
Expected: FAIL — build error CS1061 `'HotkeyButtonWidget' does not contain a definition for 'Actions'` (old test) and CS0103 `CreateAction` not found.

- [ ] **Step 3: Update the `ActionType` attribute options**

In `UtilityAndInteractiveWidgets.cs:294`, change the attribute to the 9 new options:

```csharp
    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", "Launch App", "Launch App", "Open URL", "Media Play / Pause", "Media Next", "Media Previous", "Media Stop", "Volume Up", "Volume Down", "Mute")]
    public string ActionType { get; set; } = "Launch App";
```

- [ ] **Step 4: Add the `IconFile` property**

Immediately after the `Icon` property (line 307), insert:

```csharp
    [WidgetProperty("Icon File", WidgetPropertyType.Path, "Custom SVG icon file copied into the icons folder (overrides Icon)", "")]
    public string IconFile { get; set; } = "";
```

- [ ] **Step 5: Remove toggle properties and state**

Delete the four properties (lines 321-331):

```csharp
    [WidgetProperty("Toggle Actions", WidgetPropertyType.Boolean, "Run the toggled action list after the first press", false)]
    public bool ToggleActions { get; set; }

    [WidgetProperty("Toggled Button Label", WidgetPropertyType.Text, "Label shown while toggled", "Active")]
    public string ToggledButtonLabel { get; set; } = "Active";

    [WidgetProperty("Actions", WidgetPropertyType.ActionList, "Actions run in order on the normal state")]
    public List<HotkeyAction> Actions { get; set; } = [];

    [WidgetProperty("Toggled Actions", WidgetPropertyType.ActionList, "Actions run in order on the toggled state")]
    public List<HotkeyAction> ToggledActions { get; set; } = [];
```

Delete the `_isToggled` field (line 334):

```csharp
    private bool _isToggled;
```

- [ ] **Step 6: Remove the toggle branch from `Render`**

In `Render` (line 355), replace:

```csharp
        string label = _isToggled && ToggleActions ? ToggledButtonLabel : ButtonLabel;
```

with:

```csharp
        string label = ButtonLabel;
```

- [ ] **Step 7: Rewrite `ExecuteActionsAsync` and replace `CreateLegacyAction`**

Replace the body of `ExecuteActionsAsync` (lines 427-450, keeping the `try`/`catch`/`finally` skeleton) with:

```csharp
        try
        {
            var action = CreateAction(ActionType, ActionCommand);
            if (string.IsNullOrWhiteSpace(action.Value) && action.Kind is HotkeyActionKind.Launch or HotkeyActionKind.OpenUrl)
            {
                Context?.LogError("Hotkey action skipped: Action Path/Command is empty.");
                return;
            }
            await HotkeyActionExecutor.ExecuteAsync([action], _actionCts.Token).ConfigureAwait(false);
            Context?.RequestRender();
        }
```

(Keep the existing `catch (OperationCanceledException) { }`, `catch (Exception ex) { Context?.LogError($"Hotkey action failed: {ex.Message}", ex); }`, and `finally` blocks unchanged.)

Replace `CreateLegacyAction` (lines 452-458) with:

```csharp
    internal static HotkeyAction CreateAction(string actionType, string actionCommand)
        => actionType switch
        {
            "Open URL" => new HotkeyAction { Kind = HotkeyActionKind.OpenUrl, Value = actionCommand },
            "Media Play / Pause" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "PLAYPAUSE" },
            "Media Next" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "NEXT" },
            "Media Previous" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "PREVIOUS" },
            "Media Stop" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "STOP" },
            "Volume Up" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "VOLUMEUP" },
            "Volume Down" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "VOLUMEDOWN" },
            "Mute" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "MUTE" },
            "Task Manager" => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = "taskmgr.exe" },
            _ => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = actionCommand }
        };
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx --filter "FullyQualifiedName~HotkeyWidget|FullyQualifiedName~HotkeyAction|FullyQualifiedName~ParseVirtualKey|FullyQualifiedName~MediaKeyCatalog"`
Expected: PASS — all 5 new/rewritten tests plus the kept `HotkeyAction_MediaKeySummary_UsesFriendlyName`, `ParseVirtualKey_MediaKeys_IncludeStop`, `MediaKeyCatalog_ListsSevenActionsWithFriendlyNames`, and render tests.

- [ ] **Step 9: Commit**

```bash
git add ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(widgets): single-action hotkey model with media keys"
```

---

### Task 2: Custom single-path SVG icon loader and rendering

**Files:**
- Create: `ModernWigiDash.Widgets/SvgIconLoader.cs`
- Modify: `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs:338-384` (Render icon branch)
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs` (new tests)

**Interfaces:**
- Consumes: `HotkeyButtonWidget.IconFile` (Task 1), `GriddyIcons` (unchanged).
- Produces: `public static class SvgIconLoader` (made public by user decision on 2026-08-04 because Task 4 consumes it from the App assembly; `InternalsVisibleTo` covers only Tests) with `string IconsDirectory`, `string ResolveFullPath(string iconFile)`, `string CopyToIcons(string sourcePath)`, `bool TryGetPath(string iconFile, out SKPath? path)`, `void Draw(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)`.

- [ ] **Step 1: Write the failing tests**

Add to `UnitTestSuite.cs` (inside `class UnitTestSuite`, after the tests added in Task 1):

```csharp
    private static readonly string SinglePathSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16v16H4z\"/></svg>";
    private static readonly string MultiPathSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16v16H4z\"/><path d=\"M8 8h8v8H8z\"/></svg>";

    private static string WriteTempSvg(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hw_icon_{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void HotkeyWidget_CustomSvg_ExtractsSinglePathAndRenders()
    {
        string svg = WriteTempSvg(SinglePathSvg);
        try
        {
            Assert.IsTrue(SvgIconLoader.TryGetPath(svg, out var path));
            Assert.IsNotNull(path);
            Assert.IsFalse(path!.IsEmpty);
            var widget = new HotkeyButtonWidget { IconFile = svg };
            using var surface = SKSurface.Create(new SKImageInfo(200, 150));
            widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
            Assert.IsNotNull(surface);
        }
        finally
        {
            File.Delete(svg);
        }
    }

    [TestMethod]
    public void HotkeyWidget_CustomSvg_MultiPath_FallsBackToLabelOnly()
    {
        string svg = WriteTempSvg(MultiPathSvg);
        try
        {
            Assert.IsFalse(SvgIconLoader.TryGetPath(svg, out _));
            var widget = new HotkeyButtonWidget { IconFile = svg };
            using var surface = SKSurface.Create(new SKImageInfo(200, 150));
            widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
            Assert.IsNotNull(surface);
        }
        finally
        {
            File.Delete(svg);
        }
    }

    [TestMethod]
    public void HotkeyWidget_CustomSvg_MissingFile_FallsBackToLabelOnly()
    {
        var widget = new HotkeyButtonWidget
        {
            IconFile = Path.Combine(Path.GetTempPath(), $"hw_missing_{Guid.NewGuid():N}.svg")
        };
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void HotkeyWidget_IconFile_WinsOverIcon()
    {
        string svg = WriteTempSvg(SinglePathSvg);
        try
        {
            Assert.IsTrue(SvgIconLoader.TryGetPath(svg, out var path));
            Assert.IsFalse(path!.IsEmpty);
            var widget = new HotkeyButtonWidget { Icon = "activity", IconFile = svg };
            using var surface = SKSurface.Create(new SKImageInfo(200, 150));
            widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
            Assert.IsNotNull(surface);
        }
        finally
        {
            File.Delete(svg);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx --filter "FullyQualifiedName~HotkeyWidget_CustomSvg|FullyQualifiedName~HotkeyWidget_IconFile_WinsOverIcon"`
Expected: FAIL — CS0103 `The name 'SvgIconLoader' does not exist in the current context`.

- [ ] **Step 3: Create `SvgIconLoader.cs`**

Create `ModernWigiDash.Widgets/SvgIconLoader.cs`:

```csharp
using System.Collections.Concurrent;
using System.Xml.Linq;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

public static class SvgIconLoader
{
    private static readonly ConcurrentDictionary<string, SKPath> PathCache = new(StringComparer.OrdinalIgnoreCase);

    public static string IconsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "icons");

    public static string ResolveFullPath(string iconFile)
    {
        if (string.IsNullOrWhiteSpace(iconFile)) return "";
        return Path.IsPathRooted(iconFile)
            ? iconFile
            : Path.Combine(IconsDirectory, iconFile);
    }

    public static string CopyToIcons(string sourcePath)
    {
        Directory.CreateDirectory(IconsDirectory);
        string fileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}.svg";
        File.Copy(sourcePath, Path.Combine(IconsDirectory, fileName));
        return fileName;
    }

    public static bool TryGetPath(string iconFile, out SKPath? path)
    {
        path = null;
        string fullPath = ResolveFullPath(iconFile);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return false;

        path = PathCache.GetOrAdd(fullPath, key =>
        {
            try
            {
                if (!TryExtractSinglePathData(key, out string? pathData) || string.IsNullOrWhiteSpace(pathData))
                    return new SKPath();

                SKPath? parsed = SKPath.ParseSvgPathData(pathData);
                if (parsed != null && parsed.Bounds.Width > 0 && parsed.Bounds.Height > 0)
                {
                    parsed.FillType = SKPathFillType.Winding;
                    return parsed;
                }
                parsed?.Dispose();
            }
            catch
            {
            }
            return new SKPath();
        });

        return !path.IsEmpty;
    }

    public static void Draw(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        if (sizePx <= 0 || path.IsEmpty) return;
        var bounds = path.Bounds;
        float maxDim = Math.Max(bounds.Width, bounds.Height);
        if (maxDim <= 0) return;

        float scale = sizePx / maxDim;
        canvas.Save();
        canvas.Translate(center.X + offsetX, center.Y + offsetY);
        canvas.Scale(scale, scale);
        canvas.Translate(-bounds.MidX, -bounds.MidY);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    private static bool TryExtractSinglePathData(string filePath, out string? pathData)
    {
        pathData = null;
        var doc = XDocument.Load(filePath);
        var paths = doc.Descendants().Where(e => e.Name.LocalName == "path").ToList();
        if (paths.Count != 1) return false;
        pathData = paths[0].Attribute("d")?.Value;
        return !string.IsNullOrWhiteSpace(pathData);
    }
}
```

- [ ] **Step 4: Wire custom SVG rendering into `Render`**

In `UtilityAndInteractiveWidgets.cs`, replace the block in `Render` from `if (string.IsNullOrWhiteSpace(Icon) || !GriddyIcons.Contains(Icon))` through the `GriddyIcons.Draw(...)` call (lines 357-366) with:

```csharp
        float iconSize = IconSize > 0 ? IconSize : Math.Min(bounds.Width, bounds.Height) * 0.4f;
        var iconCenter = new SKPoint(bounds.MidX, bounds.Top + Math.Max(iconSize * 0.95f, bounds.Height * 0.42f));

        if (!string.IsNullOrWhiteSpace(IconFile))
        {
            if (SvgIconLoader.TryGetPath(IconFile, out var customPath) && customPath != null)
            {
                SvgIconLoader.Draw(canvas, customPath, iconCenter, iconSize, iconColor, IconOffsetX, IconOffsetY);
            }
            else
            {
                Context?.LogError($"Hotkey custom icon file not found or unsupported: {IconFile}");
                DrawLabelOnly(canvas, bounds, label, textColor, Description);
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Icon) || !GriddyIcons.Contains(Icon))
            {
                DrawLabelOnly(canvas, bounds, label, textColor, Description);
                return;
            }
            GriddyIcons.Draw(canvas, Icon, iconCenter, iconSize, iconColor, IconOffsetX, IconOffsetY);
        }
```

The rest of `Render` (label + description drawing, lines 368-383) stays unchanged.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx --filter "FullyQualifiedName~HotkeyWidget_CustomSvg|FullyQualifiedName~HotkeyWidget_IconFile_WinsOverIcon"`
Expected: PASS — 4 tests.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.Widgets/SvgIconLoader.cs ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(widgets): render custom single-path SVG icons on hotkey widget"
```

---

### Task 3: Hide the Action / Command field for media actions

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:988-1323` (`UpdateInspectorPanel`)

**Interfaces:**
- Consumes: `HotkeyButtonWidget.ActionType` / `HotkeyButtonWidget.ActionCommand` (Task 1).
- Produces: `ActionType` dropdown whose selection toggles the `Action / Command` row visibility; `IconFile` row skipped (handled by the Icon editor in Task 4).

- [ ] **Step 1: Add tracking locals**

In `UpdateInspectorPanel`, before the `foreach (var prop in type.GetProperties())` loop (after line 992), add:

```csharp
            ComboBox? actionTypeCombo = null;
            StackPanel? actionCommandPanel = null;
```

- [ ] **Step 2: Skip the `IconFile` row**

Inside the loop, right after the `if (attr == null) continue;` check (line 996), add:

```csharp
                    if (prop.DeclaringType == typeof(HotkeyButtonWidget) &&
                        prop.Name == nameof(HotkeyButtonWidget.IconFile))
                        continue;
```

- [ ] **Step 3: Capture the ActionType combo and ActionCommand panel**

In the `Choice` branch, after the `combo` is constructed (before the `combo.SelectionChanged +=` assignment at line 1059), add:

```csharp
                        if (prop.Name == nameof(HotkeyButtonWidget.ActionType)) actionTypeCombo = combo;
```

In the `Path` branch, right after `propPanel`'s textbox row is built (after the `row` is added to `propPanel` at line 1244), add:

```csharp
                        if (prop.Name == nameof(HotkeyButtonWidget.ActionCommand)) actionCommandPanel = propPanel;
```

- [ ] **Step 4: Wire visibility after the loop**

After the closing brace of the `foreach` loop (after line 1317, still inside the `try`), add:

```csharp
            if (actionTypeCombo != null && actionCommandPanel != null)
            {
                void UpdateActionCommandVisibility()
                {
                    string? selected = actionTypeCombo.SelectedValue?.ToString();
                    actionCommandPanel.Visibility =
                        selected is "Launch App" or "Open URL" ? Visibility.Visible : Visibility.Collapsed;
                }
                actionTypeCombo.SelectionChanged += (_, _) => UpdateActionCommandVisibility();
                UpdateActionCommandVisibility();
            }
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build ModernWigiDash.slnx`
Expected: BUILD SUCCEEDED with 0 errors. Manual check (no unit test covers the App): select a Hotkey widget in the inspector, switch Action Type to a media option → the `Action / Command` row collapses; back to `Launch App` / `Open URL` → it reappears.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(app): hide hotkey Action Command field for media actions"
```

---

### Task 4: Icon editor box + Browse... popup selector

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1097-1161` (replace Icon editor branch) and add `ShowIconSelectorPopup` method.

**Interfaces:**
- Consumes: `SvgIconLoader` (Task 2), `HotkeyButtonWidget.Icon`/`IconFile` (Task 1), `GriddyIcons`, `ApplyInspectorPropertyValue` (existing, `:1336`), `ApplyDarkTitleBarToWindow` (existing), `ThemeSettings` (existing).
- Produces: `private void ShowIconSelectorPopup(PropertyInfo iconProp, HotkeyButtonWidget hotkey, TextBox box)`.

- [ ] **Step 1: Replace the Icon editor branch**

In `UpdateInspectorPanel`, replace the entire `else if (attr.PropertyType == WidgetPropertyType.Icon)` block (lines 1097-1161) with:

```csharp
                    else if (attr.PropertyType == WidgetPropertyType.Icon)
                    {
                        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                        var box = new TextBox { Text = currentVal?.ToString() ?? "" };
                        var btnBrowse = new Button { Content = "Browse\u2026", Padding = new Thickness(8, 2, 8, 2) };
                        DockPanel.SetDock(btnBrowse, Dock.Right);
                        btnBrowse.Click += (_, _) =>
                        {
                            if (_selectedWidget?.ActiveInstance is not HotkeyButtonWidget hotkey) return;
                            ShowIconSelectorPopup(prop, hotkey, box);
                        };
                        box.TextChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            ApplyInspectorPropertyValue(prop, box.Text);
                        };
                        row.Children.Add(btnBrowse);
                        row.Children.Add(box);
                        propPanel.Children.Add(row);
                    }
```

- [ ] **Step 2: Add `ShowIconSelectorPopup`**

Insert this method immediately after `UpdateInspectorPanel` (after line 1323):

```csharp
    private void ShowIconSelectorPopup(PropertyInfo iconProp, HotkeyButtonWidget hotkey, TextBox box)
    {
        var iconFileProp = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile))!;

        var dialog = new Window
        {
            Title = "Select Icon",
            Width = 520,
            Height = 620,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBackground"),
            Foreground = Brushes.White
        };
        dialog.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var search = new TextBox { ToolTip = "Search icons by name", Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(search, 0);
        root.Children.Add(search);

        var browseSvg = new Button
        {
            Content = "Browse SVG\u2026",
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var chip = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var browseRow = new StackPanel { Orientation = Orientation.Horizontal };
        browseRow.Children.Add(browseSvg);
        browseRow.Children.Add(chip);
        Grid.SetRow(browseRow, 1);
        root.Children.Add(browseRow);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        var grid = new WrapPanel { ItemWidth = 40, ItemHeight = 40 };
        scroll.Content = grid;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var selectedName = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var select = new Button
        {
            Content = "Select",
            Padding = new Thickness(14, 5, 14, 5),
            Style = (Style)FindResource("AccentButton")
        };
        Grid.SetColumn(selectedName, 0);
        Grid.SetColumn(select, 1);
        footer.Children.Add(selectedName);
        footer.Children.Add(select);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        string chosen = "";
        void UpdateSelected(string name)
        {
            chosen = name;
            selectedName.Text = name;
        }

        void RenderGrid()
        {
            grid.Children.Clear();
            string filter = search.Text?.Trim() ?? "";
            var names = string.IsNullOrEmpty(filter)
                ? GriddyIcons.Names
                : GriddyIcons.Names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var name in names)
            {
                var cell = new Button
                {
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Tag = name,
                    ToolTip = name,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Transparent
                };
                if (GriddyIcons.TryGetPathData(name, out string? pathData))
                {
                    try
                    {
                        cell.Content = new System.Windows.Shapes.Path
                        {
                            Width = 22,
                            Height = 22,
                            Stretch = Stretch.Uniform,
                            Fill = Brushes.White,
                            Data = Geometry.Parse(pathData)
                        };
                    }
                    catch
                    {
                        cell.Content = null;
                    }
                }
                if (name.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                    cell.BorderBrush = (Brush)FindResource("AccentRed");
                cell.Click += (_, _) =>
                {
                    UpdateSelected(name);
                    foreach (var child in grid.Children.OfType<Button>())
                        child.BorderBrush = Brushes.Transparent;
                    cell.BorderBrush = (Brush)FindResource("AccentRed");
                };
                grid.Children.Add(cell);
            }
        }

        search.TextChanged += (_, _) => RenderGrid();

        browseSvg.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "Select an SVG icon", Filter = "SVG files (*.svg)|*.svg" };
            if (dlg.ShowDialog() != true) return;
            if (!SvgIconLoader.TryGetPath(dlg.FileName, out _))
            {
                MessageBox.Show(dialog, "Only single-path SVG icons are supported.", "Unsupported SVG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string relative = SvgIconLoader.CopyToIcons(dlg.FileName);
            ApplyInspectorPropertyValue(iconFileProp, relative);
            ApplyInspectorPropertyValue(iconProp, "");
            hotkey.IconFile = relative;
            hotkey.Icon = "";
            chip.Text = $"Custom: {relative}";
            box.Text = relative;
            UpdateSelected(relative);
        };

        select.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (GriddyIcons.Contains(chosen))
            {
                ApplyInspectorPropertyValue(iconFileProp, "");
                ApplyInspectorPropertyValue(iconProp, chosen);
                hotkey.IconFile = "";
                hotkey.Icon = chosen;
                box.Text = chosen;
            }
            dialog.DialogResult = true;
        };

        if (!string.IsNullOrWhiteSpace(hotkey.IconFile))
        {
            chip.Text = $"Custom: {hotkey.IconFile}";
            chosen = hotkey.IconFile;
            selectedName.Text = hotkey.IconFile;
        }
        else
        {
            chosen = hotkey.Icon;
            selectedName.Text = hotkey.Icon;
        }
        RenderGrid();
        dialog.Content = root;
        dialog.ShowDialog();
    }
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build ModernWigiDash.slnx`
Expected: BUILD SUCCEEDED with 0 errors. Manual check: selecting a Hotkey widget shows the Icon box + Browse... button; Browse opens the popup; searching filters the grid; choosing a Griddy icon fills the box; Browse SVG... copies a single-path SVG into `%LocalAppData%\ModernWigiDash\icons\`, shows the chip, and sets the box; a multi-path SVG shows the "Only single-path SVG icons are supported" warning.

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(app): replace hotkey icon editor with browse popup selector"
```

---

### Task 5: Delete the legacy action-list editor and ActionList branch

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` — remove `ActionList` inspector branch (`:1174-1189`), `ShowHotkeyActionEditor` (`:1344-1526`), `CloneHotkeyAction` (`:1528-1537`).

**Interfaces:**
- Consumes: nothing (pure deletion). After this task no widget declares an `ActionList` property, so the branch is dead code.

- [ ] **Step 1: Delete the `ActionList` inspector branch**

Delete the whole `else if (attr.PropertyType == WidgetPropertyType.ActionList)` block (lines 1174-1189).

- [ ] **Step 2: Delete `ShowHotkeyActionEditor` and `CloneHotkeyAction`**

Delete the entire `ShowHotkeyActionEditor` method (lines 1344-1526) and the `CloneHotkeyAction` method (lines 1528-1537). Also delete the `using ModernWigiDash.Widgets;`... **no** — that namespace is still required for `HotkeyButtonWidget`, `GriddyIcons`, `MediaKeyCatalog`, and `IWidgetPropertyOptionsProvider` elsewhere in the file. Only the two methods go.

- [ ] **Step 3: Build and verify**

Run: `dotnet build ModernWigiDash.slnx`
Expected: BUILD SUCCEEDED with 0 errors (no remaining references to `ShowHotkeyActionEditor` or `CloneHotkeyAction`).

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test ModernWigiDash.slnx`
Expected: all tests pass except the **single known failure** `TwitchWidget_DefaultsToFontSize16AndCleanStatus` (uncommitted user WIP in `SocialAndVisualWidgets.cs`). Total passed count = previous total + 9 new/rewritten tests.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "refactor(app): remove legacy hotkey action-list editor"
```

---

## Self-Review

**Spec coverage:**
- Single action model, 9 ActionType options, Task Manager removed → Task 1.
- `IconFile` + `Icon` source A, copy-on-select into `%LocalAppData%\ModernWigiDash\icons\`, bare-file storage → Tasks 1, 2, 4.
- Single-path SVG via `SKPath.ParseSvgPathData`, no dependency, `IconColorHex` tinting → Task 2.
- Multi-path / missing file → label-only + log, no Griddy fallback, `IconFile` wins → Task 2 (tests 6-8).
- Inspector single-column: Action/Command hidden for media → Task 3; Icon box + Browse popup with search + grid + Browse SVG... + Select → Task 4.
- Removed properties tolerated by JSON deserialization; legacy `"Task Manager"` → Launch `taskmgr.exe` → Task 1 (`CreateAction`), no profile rewrite needed (design statement).
- Delete `ShowHotkeyActionEditor`, `CloneHotkeyAction`, `ActionList` branch → Task 5.
- Testing section (9 new tests + 1 rewritten) → Tasks 1-2.

**Placeholder scan:** no TBD/TODO/"add error handling"; every step carries exact code or a concrete check.

**Type consistency:** `HotkeyButtonWidget.CreateAction(string, string)` introduced in Task 1 and used identically by the Task 1 tests and the Task 1 implementation; `SvgIconLoader.TryGetPath(string, out SKPath?)`, `CopyToIcons`, `Draw`, `ResolveFullPath`, `IconsDirectory` introduced in Task 2 and consumed unchanged in Tasks 2 and 4; `IconFile` property name is consistent across Tasks 1, 2, 3, 4. Task 3 Step 3 keeps exactly two locals (`actionTypeCombo` in the `Choice` branch, `actionCommandPanel` in the `Path` branch).

---

## Post-final-review amendments (2026-08-04)

Applied after the whole-branch review returned "With fixes":

1. **Task 4 inspector Icon branch oversight:** the plan did not clear `IconFile` when the user types a Griddy icon name (or clears the box) in the inspector's Icon box, so a custom SVG kept silently winning at render and manual typing appeared to do nothing. Fixed in `ModernWigiDash.App/MainWindow.xaml.cs` by clearing `IconFile` on manual typing (via `ApplyInspectorPropertyValue(iconFileProp, "")`) and seeding the box from `IconFile` when `Icon` is empty.
2. **Test coverage:** `HotkeyWidget_ActionType_DefaultsToLaunchApp` now re-asserts the default `ButtonLabel` ("Hotkey") and `Description` ("Tap to run") from `HotkeyButtonWidget`'s property initializers.
