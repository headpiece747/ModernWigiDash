# Color Picker Everywhere — Design

- **Date:** 2026-08-12
- **Status:** Approved (pending implementation plan)
- **Scope:** Add a reusable color picker to every surface that changes a color in ModernWigiDash.

## Goal

Every place in the app that edits a color gets the same inline picker: a color swatch
plus a hex text box, with a small popup editor (presets, HSV controls, alpha, hex)
for precise picking. Three surfaces are in scope:

1. **Theme dialog** — the 19 chrome colors (WPF chrome outside the widget canvas).
2. **Inspector panel** — the 24 `[WidgetProperty(…, WidgetPropertyType.Color)]` properties
   across 12 widgets.
3. **Page/canvas background** — `PageLayout.BackgroundHexColor`, which today has **no
   editing UI** (only a code default). A swatch button in the page-tabs bar fills the gap.

## Decisions

| Decision | Choice |
|----------|--------|
| Picker style | Swatch + hex row with an inline popup editor (mockup option A). |
| Approach | Custom in-repo control — no new NuGet dependencies; matches the project's pure-model + thin-WPF pattern. |
| Alpha channel | Exposed. Picker writes `#RRGGBB` or `#AARRGGBB`; existing parsers already support both. |
| Presets | Curated preset swatches (the app palette), no per-session recents. |
| Popup commit | Sliders commit on **Apply** (one write per drag); the hex box writes **live** as typed. Cancel discards. |

## Architecture

### Core — `ModernWigiDash.Core/Theming/ColorModel.cs` (pure, testable, no WPF)

- `HsvColor(double H, double S, double V)` record struct.
- `ColorConversions` static class: `HsvToRgb`, `RgbToHsv`, hex formatting/parsing.
- The canonical hex parser stays `ThemeSettings.ParseColor` (`#RRGGBB` / `#AARRGGBB`);
  the color module delegates to it so there is exactly one parser. `RgbaColor` stays put.
- `PresetPalette` static class: curated `(name, hex)` swatches from the app palette,
  with a test-pinned invariant that every preset parses.

### App — `ModernWigiDash.App/Controls/` (thin WPF layer)

- `ColorPickerPopup` — the popup editor content: preset grid → SV square → hue slider →
  alpha slider (checkerboard) → hex field → Cancel/Apply. Owns HSV state internally;
  raises `Applied(string hex)` / `Cancelled`. `ColorChanged`-style live updates stay
  inside the popup for preview; nothing writes back until Apply.
- `ColorPickerEditor` — one reusable row control: **swatch button + hex TextBox**, hosting
  a `Popup` containing `ColorPickerPopup`. Two modes:
  - *Row mode* — inspector and theme dialog rows (swatch + hex box).
  - *Swatch-only mode* — the page-tabs background button (just the swatch).
- Popup placement: reuse the existing clamp-inside-window logic (currently
  `AttachDropdownWithinWindow` in `InspectorController`), extracted into a small shared
  helper so combo popups and the color popup stay consistent.

### Wiring into the three surfaces

1. **Inspector** — `InspectorPanelRenderer` gains `case WidgetPropertyType.Color:`
   → `BuildColorEditor`. Write-back continues through the single
   `ApplyInspectorPropertyValue` seam. No new write paths.
2. **ThemeDialog** — each of the 19 rows becomes a `ColorPickerEditor`; the dialog keeps
   its working-copy → `Validate()` → `Save()` → `_themeApplicator.Apply` flow and its
   Reset/Cancel/Apply buttons.
3. **Page background** — a swatch-only `ColorPickerEditor` next to "+ Add Page" in the
   page-tabs bar, showing the active page's background. On Apply:
   `_profile.ActivePage.BackgroundHexColor = hex` → `MarkDirty()` → `InvalidateVisual()`.

## Data Flow

### Inspector (widget colors)
- Hex box: live write-back on text change (matches today's text-editor behavior).
- Popup sliders: commit on Apply only (one write per drag); Cancel discards.
- Both paths funnel through `ApplyInspectorPropertyValue` → `SetProperty` →
  `OnPropertyChanged` (re-render) → `PersistProperty` (`PropertyValues` + profile dirty),
  so Export→Import round-trips keep working without new code.

### Theme dialog (chrome colors)
- Each editor writes into the dialog's working copy (`_entries` pattern). Dialog **Apply**
  validates every row, mutates `ThemeSettings.Theme`, `Save()`s to `app_theme.json`, and
  calls `_themeApplicator.Apply` — unchanged flow, pickers instead of raw hex text boxes.
  Reset restores defaults into the editors.

### Page background
- Apply → `page.BackgroundHexColor = hex` → `MarkDirty()` → `SkiaCanvas.InvalidateVisual()`.
- `SkiaFrameCompositor` already caches and diffs `BackgroundHexColor` per compose, so the
  new color reaches the physical WigiDash on the next 30 FPS tick — **no compositor change**.
- `PageLayout` already serializes `BackgroundHexColor`; import/export carries it automatically.

## Validation & Error Handling

- Invalid hex anywhere → red border + commit blocked (the theme dialog's existing rule,
  now shared). Widgets keep `ColorOf(hex, fallback)` as the render-path safety net.
- Corrupt `app_theme.json` → unchanged fallback to defaults.
- Empty page background → `PageLayout` setter already falls back to the default hex.
- Popup **Cancel** always discards; the row keeps its prior value.

## Testing

- `ColorModelTests` — hex↔RGB round-trips, 8-digit alpha parse/format, HSV↔RGB
  round-trips, invalid input → null, preset-palette all-parse invariant.
- `PageLayout` / `ProfileOps` — `BackgroundHexColor` normalizes (trim, empty → default)
  and survives Export→Import.
- Inspector — extend the existing editor-provider tests so a Color-typed property is
  asserted through the pure description pipeline.
- Theme — `ThemeSettingsTests` stay green (ParseColor unchanged).

## No-Regression & Physical Device

- Full suite: `dotnet test ModernWigiDash.slnx -c Release --nologo
  -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
  (existing 929 tests stay green).
- Render/transport path is **untouched**: compositor, frame pipeline, USB transport, and
  widget renderers don't change — only desktop-side input surfaces. Colors reach the
  device through the existing `ColorOf` → SKColor → compositor → RGB565 flow.
- Manual smoke list: widget color → canvas + physical display update; theme color →
  chrome updates + persists; page background → canvas + display update; export→import
  keeps every color.

## Out of Scope (YAGNI)

- No per-session "recent colors" strip.
- No eyedropper/screen sampling.
- No HSL (only HSV), no CMYK.
- No custom SVG/icon work; the preset grid is plain colored swatches.

## Conventions

- .NET 10 / current C# (records, collection expressions, pattern matching, primary
  constructors where apt). Zero new NuGet dependencies. Widget renderers and the USB
  pipeline are untouched.
