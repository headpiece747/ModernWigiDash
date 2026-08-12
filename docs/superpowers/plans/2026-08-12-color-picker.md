# Color Picker Everywhere Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every color-editing surface in ModernWigiDash (inspector widget colors, theme dialog chrome colors, page background) one reusable inline color picker: swatch + hex box + popup editor with presets, HSV controls, and alpha.

**Architecture:** A pure, WPF-free color model in Core (`HsvColor`/`ColorConversions`/`PresetPalette` on top of the existing `RgbaColor`/`ThemeSettings.ParseColor`), two thin WPF controls in App (`ColorPickerPopup` = popup editor content, `ColorPickerEditor` = the swatch+hex row that hosts it), a shared popup-clamp helper extracted from the inspector's combo-dropdown logic, and wiring into three consumers. Widget renderers, the compositor, and the USB pipeline are untouched.

**Tech Stack:** .NET 10, WPF, SkiaSharp (existing), MSTest, current C# (records, collection expressions, `field` keyword, primary constructors).

## Global Constraints

- .NET 10 / current C# idioms only; **zero new NuGet dependencies**.
- The single hex parser remains `ThemeSettings.ParseColor` (#RRGGBB / #AARRGGBB); nothing re-implements parsing.
- `RgbaColor` stays in `ModernWigiDash.Core.Theming` (record struct `(byte A, byte R, byte G, byte B)`).
- Write-back must keep routing through the existing `InspectorCallbacks.ApplyInspectorPropertyValue` seam; the renderer stays dialog-free.
- Render/transport path untouched: no changes to `SkiaFrameCompositor`, widget renderers, `ColorOf`, or the USB pipeline — colors reach the device via the existing flow.
- Existing 929 tests stay green; full suite command:
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- Test file naming: `MethodName_Scenario_ExpectedResult`. WPF-object tests run on STA via `StaRunner.Run` / `StaHost` from `TestDoubles.cs`.
- `ModernWigiDash.App` has `InternalsVisibleTo("ModernWigiDash.Tests")` (InspectorValuePolicy is internal and tested); internal test seams are fine.

---

## File Structure

| File | Responsibility |
|------|----------------|
| Create `ModernWigiDash.Core/Theming/ColorModel.cs` | Pure color model: `HsvColor`, `ColorConversions` (HSV↔RGB, hex format), `PresetSwatch`/`PresetPalette`. |
| Create `ModernWigiDash.Tests/ColorModelTests.cs` | Pure tests for conversions, hex format, preset invariant. |
| Create `ModernWigiDash.App/Controls/PopupClamp.cs` | Window-client clamping for arbitrary Popups (extracted from `InspectorController.AttachDropdownWithinWindow`'s placement callback). |
| Create `ModernWigiDash.Tests/PopupClampTests.cs` | Placement-math tests (below/above/clamped). |
| Create `ModernWigiDash.App/Controls/ColorPickerPopup.cs` | The popup editor: presets, SV square, hue/alpha strips, hex box, Apply/Cancel. |
| Create `ModernWigiDash.Tests/ColorPickerPopupTests.cs` | Popup behavior on STA (initial color, preset click, Apply/Cancel events). |
| Create `ModernWigiDash.App/Controls/ColorPickerEditor.cs` | The row control: swatch button + hex box + hosted Popup; `Applied`/`Changed` events, `IsValidHex`, `ShowHexBox`. |
| Create `ModernWigiDash.Tests/ColorPickerEditorTests.cs` | Editor behavior on STA (swatch sync, live hex write-back, invalid-input guard, swatch-only mode). |
| Modify `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs` | `case WidgetPropertyType.Color:` → `BuildColorEditor`. |
| Modify `ModernWigiDash.App/Inspector/InspectorController.cs` | Delegate popup placement to `PopupClamp` (no behavior change). |
| Modify `ModernWigiDash.Tests/InspectorEditorProviderTests.cs` | Add Color-property renderer routing test. |
| Modify `ModernWigiDash.App/Dialogs/ThemeDialog.cs` | Rows become `ColorPickerEditor`s; validation reads `IsValidHex`. |
| Create `ModernWigiDash.Tests/ThemeDialogTests.cs` | Dialog construction + validation behavior on STA. |
| Modify `ModernWigiDash.App/MainWindow.xaml` | Page-background swatch in the page-tabs bar (new column + xmlns). |
| Modify `ModernWigiDash.App/MainWindow.xaml.cs` | Wire `PageBgPicker.Applied`, refresh its Hex on page mutation. |
| Create `ModernWigiDash.Tests/PageLayoutColorTests.cs` | PageLayout normalization + export/import round-trip for `BackgroundHexColor`. |
| Modify `ModernWigiDash.Tests/MainWindowConstructionTests.cs` | Page-background picker reflects the active page's background. |

---

### Task 1: Core Color Model

**Files:**
- Create: `ModernWigiDash.Core/Theming/ColorModel.cs`
- Test: `ModernWigiDash.Tests/ColorModelTests.cs`

**Interfaces:**
- Produces (used by Tasks 3, 4, 7):
  - `public readonly record struct HsvColor(double H, double S, double V)` — H in [0,360), S/V in [0,1].
  - `public static class ColorConversions`
    - `public static RgbaColor HsvToRgb(HsvColor hsv)`
    - `public static HsvColor RgbToHsv(RgbaColor rgb)`
    - `public static string FormatHex(RgbaColor color)` — `#RRGGBB` when A==255, `#AARRGGBB` otherwise, uppercase hex digits.
  - `public readonly record struct PresetSwatch(string Name, string Hex)`
  - `public static class PresetPalette { public static IReadOnlyList<PresetSwatch> Swatches { get; } }` — the curated 12-swatch app palette, every entry parseable by `ThemeSettings.ParseColor`.

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/ColorModelTests.cs`:

```csharp
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorModelTests
{
    // ── HsvToRgb ────────────────────────────────────────────────

    [TestMethod]
    public void HsvToRgb_Red_ReturnsRed()
        => Assert.AreEqual(new RgbaColor(255, 255, 0, 0), ColorConversions.HsvToRgb(new HsvColor(0, 1, 1)));

    [TestMethod]
    public void HsvToRgb_Green_ReturnsGreen()
        => Assert.AreEqual(new RgbaColor(255, 0, 255, 0), ColorConversions.HsvToRgb(new HsvColor(120, 1, 1)));

    [TestMethod]
    public void HsvToRgb_Blue_ReturnsBlue()
        => Assert.AreEqual(new RgbaColor(255, 0, 0, 255), ColorConversions.HsvToRgb(new HsvColor(240, 1, 1)));

    [TestMethod]
    public void HsvToRgb_ZeroSaturation_ReturnsGrayscale()
        => Assert.AreEqual(new RgbaColor(255, 128, 128, 128), ColorConversions.HsvToRgb(new HsvColor(200, 0, 0.5)));

    [TestMethod]
    public void HsvToRgb_ZeroValue_ReturnsBlack()
        => Assert.AreEqual(new RgbaColor(255, 0, 0, 0), ColorConversions.HsvToRgb(new HsvColor(30, 0.5, 0)));

    // ── RgbToHsv ────────────────────────────────────────────────

    [TestMethod]
    public void RgbToHsv_Red_ReturnsRedHsv()
    {
        var hsv = ColorConversions.RgbToHsv(new RgbaColor(255, 255, 0, 0));
        Assert.AreEqual(0, hsv.H, 0.001);
        Assert.AreEqual(1, hsv.S, 0.001);
        Assert.AreEqual(1, hsv.V, 0.001);
    }

    [TestMethod]
    public void RgbToHsv_Black_ReturnsZeroHsv()
    {
        var hsv = ColorConversions.RgbToHsv(new RgbaColor(255, 0, 0, 0));
        Assert.AreEqual(0, hsv.V, 0.001);
    }

    [TestMethod]
    public void HsvToRgb_RgbToHsv_RoundTrips()
    {
        var original = new RgbaColor(255, 245, 158, 11); // #F59E0B
        var roundTripped = ColorConversions.HsvToRgb(ColorConversions.RgbToHsv(original));
        Assert.AreEqual(original.R, roundTripped.R, 1);
        Assert.AreEqual(original.G, roundTripped.G, 1);
        Assert.AreEqual(original.B, roundTripped.B, 1);
        Assert.AreEqual(original.A, roundTripped.A);
    }

    // ── FormatHex ───────────────────────────────────────────────

    [TestMethod]
    public void FormatHex_Opaque_Returns6DigitUppercase()
        => Assert.AreEqual("#F59E0B", ColorConversions.FormatHex(new RgbaColor(255, 245, 158, 11)));

    [TestMethod]
    public void FormatHex_WithAlpha_Returns8DigitUppercase()
        => Assert.AreEqual("#80F59E0B", ColorConversions.FormatHex(new RgbaColor(128, 245, 158, 11)));

    [TestMethod]
    public void FormatHex_ParseColor_RoundTrips()
    {
        var color = new RgbaColor(64, 18, 20, 29);
        var parsed = ThemeSettings.ParseColor(ColorConversions.FormatHex(color));
        Assert.AreEqual(color, parsed);
    }

    // ── Presets ─────────────────────────────────────────────────

    [TestMethod]
    public void PresetPalette_Swatches_AllParse()
        => Assert.IsTrue(PresetPalette.Swatches.All(s => ThemeSettings.ParseColor(s.Hex) is not null));

    [TestMethod]
    public void PresetPalette_Swatches_HasCuratedCount()
        => Assert.AreEqual(12, PresetPalette.Swatches.Count);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```
dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ColorModelTests"
```
Expected: FAIL — `ColorConversions` / `HsvColor` / `PresetPalette` not defined.

- [ ] **Step 3: Implement the color model**

`ModernWigiDash.Core/Theming/ColorModel.cs`:

```csharp
namespace ModernWigiDash.Core.Theming;

/// <summary>HSV color value. H in [0,360), S and V in [0,1].</summary>
public readonly record struct HsvColor(double H, double S, double V);

/// <summary>
/// Pure color conversions between HSV and <see cref="RgbaColor"/>, plus the
/// canonical hex formatter. No WPF, no Skia — testable without a UI thread.
/// Parsing stays in <see cref="ThemeSettings.ParseColor"/> (the single parser).
/// </summary>
public static class ColorConversions
{
    public static RgbaColor HsvToRgb(HsvColor hsv)
    {
        double h = hsv.H < 0 ? hsv.H % 360 + 360 : hsv.H % 360;
        double s = Math.Clamp(hsv.S, 0, 1);
        double v = Math.Clamp(hsv.V, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0),
            < 120 => (x, c, 0),
            < 180 => (0, c, x),
            < 240 => (0, x, c),
            < 300 => (x, 0, c),
            _ => (c, 0, x)
        };

        return new RgbaColor(
            255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    public static HsvColor RgbToHsv(RgbaColor rgb)
    {
        double r = rgb.R / 255.0, g = rgb.G / 255.0, b = rgb.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            h = max switch
            {
                var m when m == r => 60 * ((g - b) / delta % 6),
                var m when m == g => 60 * ((b - r) / delta + 2),
                _ => 60 * ((r - g) / delta + 4)
            };
            if (h < 0) h += 360;
        }

        double s = max == 0 ? 0 : delta / max;
        return new HsvColor(h, s, max);
    }

    /// <summary>Formats as #RRGGBB (opaque) or #AARRGGBB (with alpha), uppercase.</summary>
    public static string FormatHex(RgbaColor color)
        => color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>One curated preset swatch: a display name and its hex value.</summary>
public readonly record struct PresetSwatch(string Name, string Hex);

/// <summary>
/// The curated preset palette shown at the top of the color popup. Drawn from
/// the app's own palette so one-click picks stay theme-consistent.
/// </summary>
public static class PresetPalette
{
    public static IReadOnlyList<PresetSwatch> Swatches { get; } =
    [
        new("White", "#FAFAFA"),
        new("Zinc", "#A1A1AA"),
        new("Amber", "#F59E0B"),
        new("Highlight", "#FBBF24"),
        new("Green", "#10B981"),
        new("Emerald", "#22C55E"),
        new("Red", "#EF4444"),
        new("Blue", "#3B82F6"),
        new("Page Default", "#12141D"),
        new("App Background", "#121214"),
        new("Panel", "#1A1A1E"),
        new("Card", "#26262B")
    ];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: the same `dotnet test ... --filter "FullyQualifiedName~ColorModelTests"` command.
Expected: PASS (12/12).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Core/Theming/ColorModel.cs ModernWigiDash.Tests/ColorModelTests.cs
git commit -m "feat: core HSV color model and curated preset palette"
```

---

### Task 2: Shared Popup Clamp Helper

**Files:**
- Create: `ModernWigiDash.App/Controls/PopupClamp.cs`
- Modify: `ModernWigiDash.App/Inspector/InspectorController.cs` (the private static `AttachDropdownWithinWindow` placement callback delegates to `PopupClamp`)
- Test: `ModernWigiDash.Tests/PopupClampTests.cs`

**Interfaces:**
- Consumes: none (pure WPF math on existing types).
- Produces (used by Task 4):
  - `public static class PopupClamp`
    - `public static CustomPopupPlacement[] ComputePlacements(Size popupSize, Size targetSize, Point targetTopLeft, Size clientSize)` — the pure placement math: prefer below, then above, then clamped; **identical ordering and coordinates to the current inspector logic** (keep the inspector's tests green).
    - `public static void AttachPopupWithinWindow(Popup popup, FrameworkElement target)` — sets `PlacementMode.Custom` + a callback that resolves the window content and calls `ComputePlacements`.

- [ ] **Step 1: Extract the placement math into PopupClamp (refactor first, tests stay green)**

The current logic lives in `InspectorController.AttachDropdownWithinWindow` (`ModernWigiDash.App/Inspector/InspectorController.cs:273-323`). Replace only the `CustomPopupPlacementCallback` body so it calls `PopupClamp.ComputePlacements`, keeping the `PART_Popup` lookup and the `DropDownOpened` scroll-cap behavior untouched.

`ModernWigiDash.App/Controls/PopupClamp.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ModernWigiDash.App.Controls;

/// <summary>
/// Keeps a Popup inside the window's client area. WPF positions popups against
/// the screen, so a popup near the window's bottom edge extends below the
/// window where it can't be used. Placement prefers below, then above, then a
/// clamped fallback. Extracted from the inspector's combo-dropdown clamp so
/// combo popups and the color picker popup share one placement rule.
/// </summary>
public static class PopupClamp
{
    /// <summary>
    /// Pure placement math: candidate positions relative to the placement
    /// target, in preference order (below → above → clamped). Mirrors the
    /// pre-extraction inspector logic exactly.
    /// </summary>
    public static CustomPopupPlacement[] ComputePlacements(
        Size popupSize, Size targetSize, Point targetTopLeft, Size clientSize)
    {
        List<CustomPopupPlacement> placements = [];
        if (clientSize.Height - (targetTopLeft.Y + targetSize.Height) >= popupSize.Height)
        {
            placements.Add(new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal));
        }
        if (targetTopLeft.Y >= popupSize.Height)
        {
            placements.Add(new CustomPopupPlacement(new Point(0, -popupSize.Height), PopupPrimaryAxis.Horizontal));
        }

        double popupLeft = Math.Clamp(targetTopLeft.X, 0, Math.Max(0, clientSize.Width - popupSize.Width));
        double popupTop = Math.Clamp(targetTopLeft.Y + targetSize.Height, 0, Math.Max(0, clientSize.Height - popupSize.Height));
        placements.Add(new CustomPopupPlacement(new Point(popupLeft - targetTopLeft.X, popupTop - targetTopLeft.Y), PopupPrimaryAxis.Horizontal));
        return placements.ToArray();
    }

    /// <summary>
    /// Attaches a Custom-placement clamp to <paramref name="popup"/>: the
    /// callback resolves the target's window client area at placement time.
    /// </summary>
    public static void AttachPopupWithinWindow(Popup popup, FrameworkElement target)
    {
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            if (Window.GetWindow(target) is not Window window) return ComputePlacements(popupSize, targetSize, new Point(0, 0), new Size(window.ActualWidth, window.ActualHeight));
            if (window.Content is not FrameworkElement content) return ComputePlacements(popupSize, targetSize, new Point(0, 0), new Size(window.ActualWidth, window.ActualHeight));

            double clientW = content.ActualWidth > 0 ? content.ActualWidth : window.ActualWidth;
            double clientH = content.ActualHeight > 0 ? content.ActualHeight : window.ActualHeight;
            var tl = target.TransformToAncestor(content).Transform(new Point(0, 0));
            return ComputePlacements(popupSize, targetSize, tl, new Size(clientW, clientH));
        };
    }
}
```

In `InspectorController.cs`, replace the `CustomPopupPlacementCallback` assignment (lines ~283-303) with:

```csharp
PopupClamp.AttachPopupWithinWindow(popup, combo);
```

(`AttachPopupWithinWindow` sets `PlacementMode.Custom` and the callback itself — the helper owns the whole placement.)

...and add the `using ModernWigiDash.App.Controls;` import. **Do not change** the `PART_Popup` lookup or the `DropDownOpened` scroll cap.

- [ ] **Step 2: Verify the refactor keeps existing tests green**

Run:
```
dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~InspectorControllerTests|FullyQualifiedName~InspectorEditorProviderTests"
```
Expected: PASS.

- [ ] **Step 3: Write the placement-math tests**

`ModernWigiDash.Tests/PopupClampTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls.Primitives;
using ModernWigiDash.App.Controls;

namespace ModernWigiDash.Tests;

[TestClass]
public class PopupClampTests
{
    [TestMethod]
    public void ComputePlacements_RoomBelow_PrefersBelow()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(200, 300), new Size(100, 30), new Point(20, 40), new Size(1000, 800));

        Assert.AreEqual(new Point(0, 30), placements[0].Point); // directly below the target
        Assert.AreEqual(PopupPrimaryAxis.Horizontal, placements[0].PrimaryAxis);
    }

    [TestMethod]
    public void ComputePlacements_NoRoomBelow_PrefersAbove()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(200, 300), new Size(100, 30), new Point(20, 750), new Size(1000, 800));

        Assert.AreEqual(new Point(0, -300), placements[0].Point); // above the target
    }

    [TestMethod]
    public void ComputePlacements_NoRoomEither_ClampsToClient()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(600, 600), new Size(100, 30), new Point(500, 300), new Size(600, 400));

        var fallback = placements[^1];
        Assert.AreEqual(new Point(0, 0), fallback.Point); // clamped inside the client area
    }

    [TestMethod]
    public void ComputePlacements_AlwaysHasFallbackPlacement()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(200, 100), new Size(50, 20), new Point(10, 10), new Size(300, 200));

        Assert.IsTrue(placements.Length >= 1);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~PopupClampTests"`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Controls/PopupClamp.cs ModernWigiDash.App/Inspector/InspectorController.cs ModernWigiDash.Tests/PopupClampTests.cs
git commit -m "refactor: extract shared popup window-clamp from inspector combo logic"
```

---

### Task 3: ColorPickerPopup Control

**Files:**
- Create: `ModernWigiDash.App/Controls/ColorPickerPopup.cs`
- Test: `ModernWigiDash.Tests/ColorPickerPopupTests.cs`

**Interfaces:**
- Consumes: `HsvColor`, `ColorConversions`, `PresetPalette`, `RgbaColor`, `ThemeSettings.ParseColor` (Task 1).
- Produces (used by Task 4):
  - `public sealed class ColorPickerPopup : UserControl`
    - `public ColorPickerPopup(RgbaColor initial)` — builds the full editor UI.
    - `public RgbaColor CurrentColor { get; }` — the live preview state (getter reads the internal HSV/alpha state).
    - `public event Action<string>? Applied` — raised with the formatted hex when Apply is clicked.
    - `public event Action? Cancelled` — raised when Cancel is clicked.
    - Internal test seams: `internal Button ApplyButton { get; }`, `internal Button CancelButton { get; }`, `internal WrapPanel PresetPanel { get; }`.

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/ColorPickerPopupTests.cs`:

```csharp
using System.Linq;
using System.Windows.Controls;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorPickerPopupTests
{
    [TestMethod]
    public void Ctor_InitialColor_ExposesCurrentColor()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            Assert.AreEqual(new RgbaColor(255, 245, 158, 11), popup.CurrentColor);
        });

    [TestMethod]
    public void Apply_RaisesApplied_WithFormattedHex()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            string? applied = null;
            popup.Applied += hex => applied = hex;
            popup.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual("#F59E0B", applied);
        });

    [TestMethod]
    public void Cancel_RaisesCancelled()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            bool cancelled = false;
            popup.Cancelled += () => cancelled = true;
            popup.CancelButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.IsTrue(cancelled);
        });

    [TestMethod]
    public void Presets_ArePopulatedFromPalette()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            Assert.AreEqual(PresetPalette.Swatches.Count, popup.PresetPanel.Children.Count);
        });
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ColorPickerPopupTests"`
Expected: FAIL — `ColorPickerPopup` not defined.

- [ ] **Step 3: Implement the popup control**

`ModernWigiDash.App/Controls/ColorPickerPopup.cs` (full file):

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Controls;

/// <summary>
/// The color picker popup editor: curated presets, an SV square, hue and alpha
/// strips, a hex field, and Apply/Cancel. Pure preview state inside — nothing
/// is committed until Apply (or the hosting row's live hex box) raises Applied.
/// </summary>
public sealed class ColorPickerPopup : UserControl
{
    private HsvColor _hsv;
    private byte _alpha = 255;
    private readonly Canvas _svCanvas;
    private readonly Rectangle _svHueLayer;
    private readonly Rectangle _svWhiteLayer;
    private readonly Rectangle _svBlackLayer;
    private readonly Border _svThumb;
    private readonly Canvas _hueCanvas;
    private readonly Border _hueThumb;
    private readonly Slider _alphaSlider;
    private readonly TextBox _hexBox;
    private bool _suppress;

    /// <summary>Apply / Cancel buttons exposed for tests.</summary>
    internal Button ApplyButton { get; }
    internal Button CancelButton { get; }
    internal WrapPanel PresetPanel { get; }

    public ColorPickerPopup(RgbaColor initial)
    {
        _hsv = ColorConversions.RgbToHsv(initial);
        _alpha = initial.A;

        var root = new StackPanel { Width = 252 };

        // Presets
        PresetPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var swatch in PresetPalette.Swatches)
        {
            var btn = new Button
            {
                Content = "",
                Width = 22,
                Height = 18,
                Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(HexToMediaColor(swatch.Hex)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                ToolTip = swatch.Name
            };
            btn.Click += (_, _) => SetFromHex(swatch.Hex);
            PresetPanel.Children.Add(btn);
        }
        root.Children.Add(PresetPanel);

        // SV square: hue base + white (horizontal) + black (vertical) overlays
        _svCanvas = new Canvas { Width = 252, Height = 130 };
        _svHueLayer = new Rectangle { Width = 252, Height = 130, IsHitTestVisible = false };
        _svWhiteLayer = new Rectangle
        {
            Width = 252, Height = 130, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, 0)
        };
        _svBlackLayer = new Rectangle
        {
            Width = 252, Height = 130, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90)
        };
        _svThumb = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            Background = Brushes.Transparent, IsHitTestVisible = false
        };
        Canvas.SetLeft(_svThumb, -7); Canvas.SetTop(_svThumb, -7);
        _svCanvas.Children.Add(_svHueLayer);
        _svCanvas.Children.Add(_svWhiteLayer);
        _svCanvas.Children.Add(_svBlackLayer);
        _svCanvas.Children.Add(_svThumb);
        root.Children.Add(_svCanvas);

        // Hue strip
        _hueCanvas = new Canvas { Width = 252, Height = 16, Margin = new Thickness(0, 10, 0, 0) };
        var hueStrip = new Rectangle
        {
            Width = 252, Height = 16,
            Fill = new LinearGradientBrush(
            [
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Yellow, 1d / 6),
                new GradientStop(Colors.Lime, 2d / 6),
                new GradientStop(Colors.Cyan, 3d / 6),
                new GradientStop(Colors.Blue, 4d / 6),
                new GradientStop(Colors.Magenta, 5d / 6),
                new GradientStop(Colors.Red, 1)
            ], 0)
        };
        _hueThumb = new Border
        {
            Width = 10, Height = 16, BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            Background = Brushes.Transparent, IsHitTestVisible = false
        };
        Canvas.SetLeft(_hueThumb, -5);
        _hueCanvas.Children.Add(hueStrip);
        _hueCanvas.Children.Add(_hueThumb);
        root.Children.Add(_hueCanvas);

        // Alpha slider
        var alphaLabel = new TextBlock { Text = "Opacity", FontSize = 10, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 2) };
        _alphaSlider = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true };
        _alphaSlider.ValueChanged += (_, e) => { _alpha = (byte)e.NewValue; UpdatePreview(); };
        root.Children.Add(alphaLabel);
        root.Children.Add(_alphaSlider);

        // Hex field + buttons
        var hexRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _hexBox = new TextBox { Text = ColorConversions.FormatHex(initial) };
        _hexBox.TextChanged += (_, _) => { if (!_suppress) SetFromHex(_hexBox.Text); };
        CancelButton = new Button { Content = "Cancel", Padding = new Thickness(8, 2, 8, 2) };
        ApplyButton = new Button { Content = "Apply", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(CancelButton, Dock.Right);
        DockPanel.SetDock(ApplyButton, Dock.Right);
        hexRow.Children.Add(CancelButton);
        hexRow.Children.Add(ApplyButton);
        hexRow.Children.Add(_hexBox);
        root.Children.Add(hexRow);

        ApplyButton.Click += (_, _) => Applied?.Invoke(ColorConversions.FormatHex(CurrentColor));
        CancelButton.Click += (_, _) => Cancelled?.Invoke();

        // Interaction
        _svCanvas.MouseLeftButtonDown += (_, e) => { _svCanvas.CaptureMouse(); UpdateSvFromPoint(e.GetPosition(_svCanvas)); };
        _svCanvas.MouseMove += (_, e) => { if (_svCanvas.IsMouseCaptured) UpdateSvFromPoint(e.GetPosition(_svCanvas)); };
        _svCanvas.MouseLeftButtonUp += (_, _) => _svCanvas.ReleaseMouseCapture();
        _hueCanvas.MouseLeftButtonDown += (_, e) => { _hueCanvas.CaptureMouse(); UpdateHueFromPoint(e.GetPosition(_hueCanvas)); };
        _hueCanvas.MouseMove += (_, e) => { if (_hueCanvas.IsMouseCaptured) UpdateHueFromPoint(e.GetPosition(_hueCanvas)); };
        _hueCanvas.MouseLeftButtonUp += (_, _) => _hueCanvas.ReleaseMouseCapture();

        Content = root;
        UpdatePreview();
    }

    /// <summary>The live preview color from the current HSV/alpha state.</summary>
    public RgbaColor CurrentColor
    {
        get
        {
            var rgb = ColorConversions.HsvToRgb(_hsv);
            return rgb with { A = _alpha };
        }
    }

    public event Action<string>? Applied;
    public event Action? Cancelled;

    private void UpdateSvFromPoint(Point p)
    {
        _hsv = _hsv with
        {
            S = Math.Clamp(p.X / _svCanvas.Width, 0, 1),
            V = Math.Clamp(1 - p.Y / _svCanvas.Height, 0, 1)
        };
        UpdatePreview();
    }

    private void UpdateHueFromPoint(Point p)
    {
        _hsv = _hsv with { H = Math.Clamp(p.X / _hueCanvas.Width, 0, 1) * 360 };
        UpdatePreview();
    }

    private void SetFromHex(string hex)
    {
        if (ThemeSettings.ParseColor(hex) is not { } color) return;
        _hsv = ColorConversions.RgbToHsv(color);
        _alpha = color.A;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var rgb = ColorConversions.HsvToRgb(_hsv);
        _svHueLayer.Fill = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        Canvas.SetLeft(_svThumb, _svCanvas.Width * _hsv.S - 7);
        Canvas.SetTop(_svThumb, _svCanvas.Height * (1 - _hsv.V) - 7);
        Canvas.SetLeft(_hueThumb, _hueCanvas.Width * (_hsv.H / 360) - 5);
        if (_alphaSlider != null && _alphaSlider.Value != _alpha) _alphaSlider.Value = _alpha;

        _suppress = true;
        _hexBox.Text = ColorConversions.FormatHex(CurrentColor);
        _suppress = false;
    }

    private static Color HexToMediaColor(string hex)
        => ThemeSettings.ParseColor(hex) is { } c
            ? Color.FromArgb(c.A, c.R, c.G, c.B)
            : Colors.Black;
}
```

Note: the `LinearGradientBrush` collection-expression overload requires the stops as a collection; if the compiler rejects the collection expression for `GradientStopCollection`, use explicit `Add` calls — keep behavior identical.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ColorPickerPopupTests"`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Controls/ColorPickerPopup.cs ModernWigiDash.Tests/ColorPickerPopupTests.cs
git commit -m "feat: color picker popup editor with presets, HSV, and alpha"
```

---

### Task 4: ColorPickerEditor Row Control

**Files:**
- Create: `ModernWigiDash.App/Controls/ColorPickerEditor.cs`
- Test: `ModernWigiDash.Tests/ColorPickerEditorTests.cs`

**Interfaces:**
- Consumes: `ColorPickerPopup` (Task 3), `PopupClamp` (Task 2), `ThemeSettings.ParseColor`.
- Produces (used by Tasks 5, 6, 7):
  - `public sealed class ColorPickerEditor : UserControl`
    - `public ColorPickerEditor()` — parameterless (XAML-constructible for the page bar).
    - `public string Hex { get; set; }` — current value; setter updates swatch + hex box **without raising events** (panel rebuild).
    - `public bool ShowHexBox { get; set; } = true` — false = swatch-only mode.
    - `public bool IsValidHex { get; private set; }` — current text parses.
    - `public event Action<string>? Applied` — commit: popup Apply **or** a valid live hex-box change.
    - `public event Action? Changed` — every hex-box text change (theme-dialog validation hook).
    - Internal test seams: `internal TextBox HexBox { get; }`, `internal Button SwatchButton { get; }`, `internal Popup Popup { get; }`.

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/ColorPickerEditorTests.cs`:

```csharp
using System.Windows.Media;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorPickerEditorTests
{
    [TestMethod]
    public void HexSetter_UpdatesSwatchBrush()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            editor.Hex = "#F59E0B";
            var brush = editor.SwatchButton.Background as SolidColorBrush;
            Assert.AreEqual(Color.FromRgb(245, 158, 11), brush!.Color);
        });

    [TestMethod]
    public void ValidHexTextChange_RaisesApplied()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            string? applied = null;
            editor.Applied += hex => applied = hex;
            editor.HexBox.Text = "#00FF00";
            Assert.AreEqual("#00FF00", applied);
        });

    [TestMethod]
    public void InvalidHexText_DoesNotRaiseApplied_AndFlagsInvalid()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            bool applied = false;
            int changed = 0;
            editor.Applied += _ => applied = true;
            editor.Changed += () => changed++;
            editor.HexBox.Text = "not-a-color";
            Assert.IsFalse(applied);
            Assert.IsFalse(editor.IsValidHex);
            Assert.AreEqual(1, changed);
        });

    [TestMethod]
    public void ShowHexBoxFalse_HidesHexBox()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor { ShowHexBox = false };
            Assert.AreEqual(System.Windows.Visibility.Collapsed, editor.HexBox.Visibility);
        });

    [TestMethod]
    public void PopupApply_RaisesApplied_WithPopupHex()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor { Hex = "#F59E0B" };
            string? applied = null;
            editor.Applied += hex => applied = hex;
            editor.PopupContent.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual("#F59E0B", applied);
        });
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ColorPickerEditorTests"`
Expected: FAIL — `ColorPickerEditor` not defined.

- [ ] **Step 3: Implement the editor control**

`ModernWigiDash.App/Controls/ColorPickerEditor.cs` (full file):

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Controls;

/// <summary>
/// One reusable color-editing row: a swatch button and (optionally) a hex
/// text box, hosting a <see cref="ColorPickerPopup"/> in a clamped Popup.
/// Commits surface through <see cref="Applied"/> — the popup's Apply or a
/// valid live hex-box change — so consumers (inspector, theme dialog, page
/// background) all wire one event. Programmatic <see cref="Hex"/> sets never
/// raise events (panel rebuilds must not commit).
/// </summary>
public sealed class ColorPickerEditor : UserControl
{
    private readonly Border _swatch;
    private string _hex = "";
    private bool _suppress;

    internal TextBox HexBox { get; }
    internal Button SwatchButton { get; }
    internal Popup Popup { get; }
    internal ColorPickerPopup PopupContent { get; }

    public ColorPickerEditor()
    {
        var row = new DockPanel();

        SwatchButton = new Button
        {
            Width = 34, Height = 24, Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(0), BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1), ToolTip = "Pick a color"
        };
        _swatch = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(3) };
        SwatchButton.Content = _swatch;

        HexBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };

        row.Children.Add(SwatchButton);
        row.Children.Add(HexBox);

        PopupContent = new ColorPickerPopup(new RgbaColor(255, 0, 0, 0));
        Popup = new Popup
        {
            PlacementTarget = SwatchButton,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = PopupContent
            }
        };

        SwatchButton.Click += (_, _) =>
        {
            PopupContent.SetFromHex(_hex); // internal reuse: sync popup state
            PopupClamp.AttachPopupWithinWindow(Popup, SwatchButton);
            Popup.IsOpen = true;
        };

        HexBox.TextChanged += (_, _) =>
        {
            if (_suppress) return;
            _hex = HexBox.Text.Trim();
            IsValidHex = ThemeSettings.ParseColor(_hex) is not null;
            HexBox.BorderBrush = IsValidHex ? null : Brushes.Red;
            SyncSwatch();
            Changed?.Invoke();
            if (IsValidHex) Applied?.Invoke(_hex);
        };

        PopupContent.Applied += hex =>
        {
            Popup.IsOpen = false;
            SetHexSilently(hex);
            IsValidHex = true;
            Applied?.Invoke(hex);
        };
        PopupContent.Cancelled += () => Popup.IsOpen = false;

        Content = row;
    }

    /// <summary>Current hex value (#RRGGBB or #AARRGGBB). Setting it updates the
    /// swatch and hex box without raising <see cref="Applied"/>.</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            _hex = value.Trim();
            IsValidHex = ThemeSettings.ParseColor(_hex) is not null;
            SetHexSilently(_hex);
            SyncSwatch();
        }
    }

    /// <summary>False hides the hex box (swatch-only mode for the page bar).</summary>
    public bool ShowHexBox
    {
        get => HexBox.Visibility == Visibility.Visible;
        set => HexBox.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsValidHex { get; private set; }

    /// <summary>Raised on commit: popup Apply, or a valid live hex-box change.</summary>
    public event Action<string>? Applied;

    /// <summary>Raised on every hex-box text change (validation hook).</summary>
    public event Action? Changed;

    private void SetHexSilently(string hex)
    {
        _suppress = true;
        try { HexBox.Text = hex; }
        finally { _suppress = false; }
    }

    private void SyncSwatch()
    {
        var color = ThemeSettings.ParseColor(_hex);
        _swatch.Background = color is { } c
            ? new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B))
            : new SolidColorBrush(Color.FromRgb(18, 20, 29));
    }
}
```

**Note:** `ColorPickerPopup.SetFromHex` is currently private — add `internal void SetFromHex(string hex)` to `ColorPickerPopup.cs` (Task 3 file) so the editor can seed the popup with the row's current color on open.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ColorPickerEditorTests|FullyQualifiedName~ColorPickerPopupTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Controls/ColorPickerEditor.cs ModernWigiDash.App/Controls/ColorPickerPopup.cs ModernWigiDash.Tests/ColorPickerEditorTests.cs
git commit -m "feat: reusable color picker editor row with live hex write-back"
```

---

### Task 5: Inspector Color Editor

**Files:**
- Modify: `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs` (add `case WidgetPropertyType.Color` + `BuildColorEditor`; update the `default` comment)
- Modify: `ModernWigiDash.Tests/InspectorEditorProviderTests.cs` (add a Color property to the provider widget + a renderer routing test)

**Interfaces:**
- Consumes: `ColorPickerEditor` (Task 4), `InspectorCallbacks.ApplyInspectorPropertyValue` (existing seam).
- Produces: nothing new — the inspector now renders a `ColorPickerEditor` for `WidgetPropertyType.Color` properties.

- [ ] **Step 1: Write the failing test**

In `ModernWigiDash.Tests/InspectorEditorProviderTests.cs`:

1. Add a Color property to `ProviderWidget` (after `Label`):

```csharp
[WidgetProperty("Accent", WidgetPropertyType.Color, defaultValue: "#F59E0B")]
public string AccentHex { get; set; } = "#F59E0B";
```

2. Update the `Describe_SkipsIconPickerCompanion_KeepsPickerAndCommand` expected list from `["Label", "Icon", "ActionType", "ActionCommand"]` to `["Label", "AccentHex", "Icon", "ActionType", "ActionCommand"]` (the assertion compares `Property.Name`, so the new property's actual name `AccentHex` goes in the list).

3. Add a renderer routing test at the end of the class:

```csharp
[TestMethod]
public void Render_ColorProperty_BuildsColorPickerEditor_AndWritesBackThroughSeam()
{
    StaRunner.Run(() =>
    {
        System.Reflection.PropertyInfo? writtenProp = null;
        object? writtenValue = null;

        var placed = Place();
        var panel = new StackPanel();
        InspectorPanelRenderer.Render(
            placed,
            InspectorModelBuilder.Describe(placed),
            panel.Children,
            () => false,
            new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (prop, value) => { writtenProp = prop; writtenValue = value; },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null
            });

        var editor = panel.Children.OfType<StackPanel>()
            .SelectMany(sp => sp.Children.OfType<ModernWigiDash.App.Controls.ColorPickerEditor>())
            .Single();

        Assert.AreEqual("#F59E0B", editor.Hex);
        editor.HexBox.Text = "#00FF00"; // live write-back
        Assert.AreEqual(nameof(ProviderWidget.AccentHex), writtenProp?.Name);
        Assert.AreEqual("#00FF00", writtenValue);
    });
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~InspectorEditorProviderTests"`
Expected: the new test fails (no ColorPickerEditor in the panel — the renderer falls through to the text editor).

- [ ] **Step 3: Implement the renderer case**

In `ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs`:

1. Add to the switch (before `case WidgetPropertyType.Path:`):

```csharp
case WidgetPropertyType.Color:
    propPanel.Children.Add(BuildColorEditor(desc, isUpdatingInspector, callbacks));
    break;
```

2. Update the `default` comment from `// Text, Number, or Color` to `// Text or Number`.

3. Add the builder (next to `BuildTextEditor`):

```csharp
private static UIElement BuildColorEditor(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
{
    var editor = new ColorPickerEditor
    {
        Hex = desc.CurrentValue?.ToString() ?? ""
    };
    editor.Applied += hex =>
    {
        if (isUpdatingInspector()) return;
        callbacks.ApplyInspectorPropertyValue(desc.Property, hex);
    };
    return editor;
}
```

4. Add `using ModernWigiDash.App.Controls;` to the file.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~InspectorEditorProviderTests|FullyQualifiedName~InspectorModelBuilderTests|FullyQualifiedName~InspectorValuePolicyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs ModernWigiDash.Tests/InspectorEditorProviderTests.cs
git commit -m "feat: inspector renders color picker editors for widget color properties"
```

---

### Task 6: Theme Dialog Color Picker Rows

**Files:**
- Modify: `ModernWigiDash.App/Dialogs/ThemeDialog.cs` (rows become `ColorPickerEditor`s; validation via `IsValidHex`)
- Test: `ModernWigiDash.Tests/ThemeDialogTests.cs`

**Interfaces:**
- Consumes: `ColorPickerEditor` (Task 4), `ThemeSettings.StringProperties` (existing).
- Produces: nothing new. The dialog's `_entries` becomes `List<(string Key, ColorPickerEditor Editor)>`.

- [ ] **Step 1: Write the failing test**

`ModernWigiDash.Tests/ThemeDialogTests.cs`:

```csharp
using System.Linq;
using System.Windows;
using ModernWigiDash.App.Controls;
using ModernWigiDash.App.Dialogs;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ThemeDialogTests
{
    private static readonly StaHost Host = new("ThemeDialogTests-STA");

    [TestMethod]
    public void Ctor_BuildsOneColorEditorPerThemeProperty()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            var editors = dialog.FindVisualChildren<ColorPickerEditor>().ToList();
            Assert.AreEqual(ThemeSettings.StringProperties.Count, editors.Count);
            return null;
        });

    [TestMethod]
    public void InvalidHex_DisablesApply()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            var editor = dialog.FindVisualChildren<ColorPickerEditor>().First();
            editor.HexBox.Text = "zzz";
            Assert.IsFalse(dialog.ApplyIsEnabledForTest);
            return null;
        });

    [TestMethod]
    public void ValidHex_EnablesApply()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            var editor = dialog.FindVisualChildren<ColorPickerEditor>().First();
            editor.HexBox.Text = "#F59E0B";
            Assert.IsTrue(dialog.ApplyIsEnabledForTest);
            return null;
        });
}
```

**Note:** `ThemeDialog.ApplyIsEnabledForTest` and `FindVisualChildren` don't exist yet — Step 3 adds them as internal test seams (mirroring `InspectorController.FindVisualChild`). If `_btnApply` visibility differs, expose `internal bool ApplyIsEnabledForTest => _btnApply.IsEnabled;` and the visual-tree walk `internal static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ThemeDialogTests"`
Expected: FAIL — compile errors (`ColorPickerEditor` row not built; missing test seams).

- [ ] **Step 3: Convert the dialog rows to color picker editors**

In `ModernWigiDash.App/Dialogs/ThemeDialog.cs`:

1. Change the field type:

```csharp
private readonly List<(string Key, ColorPickerEditor Editor)> _entries = [];
```

2. In the row loop, replace `var box = new TextBox { Text = current };` with:

```csharp
var editor = new ColorPickerEditor { Hex = current };
editor.Changed += (_, _) => Validate();
row.Children.Add(editor);
```

(remove the `row.Children.Add(box);` line that follows).

3. Update the event wiring loop:

```csharp
foreach (var (_, editor) in _entries)
{
    editor.Changed += (_, _) => Validate();
}
```

(remove the `box.TextChanged` / `box.LostFocus` hooks).

4. `Validate()` becomes:

```csharp
private void Validate()
{
    bool valid = _entries.All(e => e.Editor.IsValidHex);
    _btnApply.IsEnabled = valid;
}
```

5. `btnReset.Click` handler becomes:

```csharp
var defaults = new ThemeSettings();
foreach (var (key, editor) in _entries)
    editor.Hex = (string?)defaults.GetType().GetProperty(key)?.GetValue(defaults) ?? "#000000";
```

6. `ApplyFromDialog()` becomes:

```csharp
private void ApplyFromDialog()
{
    foreach (var (key, editor) in _entries)
    {
        if (ThemeSettings.ParseColor(editor.Hex) is not null)
            ThemeSettings.Theme.GetType().GetProperty(key)?.SetValue(ThemeSettings.Theme, editor.Hex);
    }
    if (!ThemeSettings.Save()) { /* unchanged warning */ }
    _themeApplicator.Apply(this);
    Close();
}
```

7. Add the test seams and imports at the bottom of the class (and `using ModernWigiDash.App.Controls;`, `using System.Windows.Media;` if not present):

```csharp
internal bool ApplyIsEnabledForTest => _btnApply.IsEnabled;

internal static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
{
    int count = VisualTreeHelper.GetChildrenCount(parent);
    for (int i = 0; i < count; i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is T match) yield return match;
        foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~ThemeDialogTests"`
Expected: PASS (3/3). Then run the inspector filter from Task 5 again to confirm no cross-breakage.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Dialogs/ThemeDialog.cs ModernWigiDash.Tests/ThemeDialogTests.cs
git commit -m "feat: theme dialog uses color picker editors for chrome colors"
```

---

### Task 7: Page Background Picker

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml` (page-tabs bar: new column + `ColorPickerEditor` swatch)
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` (wire `PageBgPicker.Applied`, refresh its Hex in `RefreshAfterMutation`)
- Test: `ModernWigiDash.Tests/PageLayoutColorTests.cs`
- Modify: `ModernWigiDash.Tests/MainWindowConstructionTests.cs` (assert the picker reflects the active page background)

**Interfaces:**
- Consumes: `ColorPickerEditor` (Task 4), `_profile.ActivePage.BackgroundHexColor`, `_profilePersistence.MarkDirty()`, `SkiaCanvas.InvalidateVisual()`.
- Produces: `PageBgPicker` (internal XAML field) wired to `Applied`.

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/PageLayoutColorTests.cs`:

```csharp
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Tests;

[TestClass]
public class PageLayoutColorTests
{
    [TestMethod]
    public void BackgroundHexColor_SetsNormalizedValue()
    {
        var page = new PageLayout { BackgroundHexColor = " #A1B2C3 " };
        Assert.AreEqual("#A1B2C3", page.BackgroundHexColor);
    }

    [TestMethod]
    public void BackgroundHexColor_Empty_FallsBackToDefault()
    {
        var page = new PageLayout { BackgroundHexColor = "   " };
        Assert.AreEqual(PageLayout.DefaultBackgroundHexColor, page.BackgroundHexColor);
    }

    [TestMethod]
    public void BackgroundHexColor_ExportImport_RoundTrips()
    {
        var profile = new ProfileLayout();
        profile.Pages[0].BackgroundHexColor = "#F59E0B";

        var json = ProfileOps.ExportJson(profile);
        var imported = ProfileOps.ImportJson(json, new WidgetPluginLoader(), new TestContext());

        Assert.AreEqual("#F59E0B", imported!.Pages[0].BackgroundHexColor);
    }
}
```

**Note:** `ImportJson` requires a loader + context: `ProfileOps.ImportJson(json, new WidgetPluginLoader(), new TestContext())` (see `ProfileOps.cs:351`; the JSON contains no widgets, so an empty loader suffices — the pattern used by `ProfileOpsTests`).

Add to `ModernWigiDash.Tests/MainWindowConstructionTests.cs` — using the **internal 2-arg ctor with a temp profile path** (the 1-arg ctor loads the real LocalAppData profile, which would make the assertion environment-dependent), and the file's existing `Host.Invoke` + close pattern:

```csharp
[TestMethod]
public void Construct_PageBackgroundPicker_ReflectsStarterPageBackground()
{
    var (hex, error) = Host.Invoke(() =>
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wmd-bg-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(new StubPresentMonNative(), Path.Combine(tempDir, "profile.json"));
        try
        {
            return (object?)window.PageBgPicker.Hex;
        }
        finally
        {
            window.Close();
        }
    });

    Assert.IsNull(error, error?.ToString());
    Assert.AreEqual(ModernWigiDash.Core.Models.PageLayout.DefaultBackgroundHexColor, hex);
}
```

(Needs `using System.IO;` if not already present. The starter profile — created when no profile file exists — uses the default background, so the picker must show `#12141D`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~PageLayoutColorTests|FullyQualifiedName~MainWindowConstructionTests"`
Expected: FAIL — `PageBgPicker` not defined.

- [ ] **Step 3: Add the picker to the page-tabs bar (XAML)**

In `ModernWigiDash.App/MainWindow.xaml`:

1. Add the controls namespace to the root `Window` element:

```xml
xmlns:controls="clr-namespace:ModernWigiDash.App.Controls"
```

2. In the page-tabs `Grid` (lines ~143-154), add a third column and the swatch after the Add Page button:

```xml
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="Auto"/>
    <ColumnDefinition Width="Auto"/>
</Grid.ColumnDefinitions>
...
<Button x:Name="BtnAddPage" Grid.Column="1" Content="+ Add Page" Margin="12,0,0,0" Padding="12,6" FontSize="12" VerticalAlignment="Center" Click="BtnAddPage_Click"/>
<controls:ColorPickerEditor x:Name="PageBgPicker" Grid.Column="2" ShowHexBox="False"
                            Margin="12,0,0,0" VerticalAlignment="Center"/>
```

- [ ] **Step 4: Wire it in code-behind**

In `ModernWigiDash.App/MainWindow.xaml.cs`:

1. In the constructor, after the page-tabs wiring block (after `_pageTabs = new PageTabsView(...)`):

```csharp
PageBgPicker.Applied += OnPageBackgroundApplied;
```

2. Add the handler and a refresh helper:

```csharp
/// <summary>Page-background picker commit: writes the active page's
/// BackgroundHexColor (the compositor diffs it per frame, so the change
/// flows to the physical display on the next tick) and marks the profile
/// dirty. The swatch itself is kept in sync by <see cref="RefreshAfterMutation"/>.</summary>
private void OnPageBackgroundApplied(string hex)
{
    _profile.ActivePage.BackgroundHexColor = hex;
    _profilePersistence.MarkDirty();
    SkiaCanvas.InvalidateVisual();
}
```

3. In `RefreshAfterMutation` (MainWindow.xaml.cs:299), add at the end:

```csharp
PageBgPicker.Hex = _profile.ActivePage.BackgroundHexColor;
```

(`RefreshAfterMutation` runs on page switch/add/delete/rename/import — the single mutation funnel, so the swatch always tracks the active page.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~PageLayoutColorTests|FullyQualifiedName~MainWindowConstructionTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml ModernWigiDash.App/MainWindow.xaml.cs ModernWigiDash.Tests/PageLayoutColorTests.cs ModernWigiDash.Tests/MainWindowConstructionTests.cs
git commit -m "feat: page background color picker in the page-tabs bar"
```

---

### Task 8: Full Verification & Smoke Check

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite**

Run:
```
dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false
```
Expected: all tests pass (existing 929 + new ~30). If any fail, fix before proceeding — no exceptions.

- [ ] **Step 2: Build the Release binary**

Run:
```
dotnet build ModernWigiDash.slnx -c Release --nologo
```
Expected: 0 errors, 0 warnings introduced by this work.

- [ ] **Step 3: Manual smoke on the physical WigiDash** (requires elevation + attached device)

Run the app via the elevated launcher: `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elevated.ps1`

1. **Inspector:** select a widget with a color property (e.g. Clock → Accent Color); change it via the popup → canvas **and physical display** update; type an invalid hex → red border, no write; type a valid hex → live update.
2. **Theme dialog:** open Theme Customization; pick a chrome color → window chrome updates on Apply; `app_theme.json` next to the exe contains the new value; Cancel leaves the theme untouched.
3. **Page background:** click the new swatch in the page-tabs bar → canvas + display background change; switch pages → swatch tracks each page's own background; restart the app → background persists.
4. **Alpha:** pick a semi-transparent color (8-digit) on a widget → renders with alpha on the display.
5. **Export→Import:** export the profile, import it back → every color survives.

- [ ] **Step 4: Final commit if the smoke pass surfaced any tweaks**

```bash
git add -A
git commit -m "fix: color picker smoke-pass adjustments"
```

---

## Self-Review Notes

- **Spec coverage:** Core model (Task 1) ↔ spec's Core section; popup clamp shared helper (Task 2) ↔ spec's "extracted into a small shared helper"; popup + editor controls (Tasks 3-4) ↔ spec's App controls; inspector wiring (Task 5), theme dialog (Task 6), page background (Task 7) ↔ spec's three wirings; live hex write-back + popup-Apply-commit (Tasks 4-5) ↔ spec's data-flow section; validation/red-border (Tasks 4, 6) ↔ spec's error handling; tests (all tasks) + full suite (Task 8) ↔ spec's testing/no-regression sections; zero new dependencies throughout ↔ global constraint.
- **Type consistency:** `ColorConversions.HsvToRgb/RgbToHsv/FormatHex`, `PresetPalette.Swatches`, `ColorPickerPopup.Applied/Cancelled/CurrentColor`, `ColorPickerEditor.Hex/ShowHexBox/IsValidHex/Applied/Changed` — single definitions, reused by every later task; `RgbaColor` (existing) is the one color value type.
