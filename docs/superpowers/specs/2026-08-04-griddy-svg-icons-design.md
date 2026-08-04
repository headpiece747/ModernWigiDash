# Griddy SVG Icons Design

**Date:** 2026-08-04
**Status:** Approved

## Goal

Replace the bundled Material Symbols Rounded icon font with the Griddy Icons SVG library as the sole icon source for dashboard widgets. Full set (1,145 icons, regular/line style), rendered as Skia paths, with a searchable icon picker. Remove all Material font assets, generated codepoints, and `IconLibrary`.

## Decisions (user-confirmed)

- **Replace** Material entirely; griddy is the only icon source. No migration: saved Material icon names (e.g. `power_settings_new`) render nothing.
- **Full set**: all 1,145 griddy icons, **regular (line)** style only.
- **Searchable picker** so the full set stays usable.
- No new NuGet packages. Tests stay fully offline. Build stays offline (data committed).

## Licensing

- Griddy Icons is **MIT** (c) 2025 Griddy Icons (verified at `github.com/griddy-icons/griddy-icons`). Bundling, modification, and redistribution are permitted with the license text included.
- Commit `LICENSE-GriddyIcons.txt` (the MIT text) next to the generated icon data.

## Asset layout (verified)

- Icons live at `icons-src/regular/*.svg` (and `icons-src/filled/*.svg`) in the repo; naming is kebab-case (`activity.svg`, `address-book.svg`).
- Each SVG is 24×24 and contains one or more `<path d="…" fill="#202023"/>` elements. Line style is fill-based; the fill color is overridden at render time by the widget's icon color.
- Individual SVGs are freely available under MIT; no paid download required.

## Architecture

### 1. Data pipeline (one-time, artifacts committed)

1. Shallow-clone `https://github.com/griddy-icons/griddy-icons` (depth 1) into a temp dir.
2. Copy `icons-src/regular/*.svg` locally.
3. Generation script (`scripts/griddy-generate.ps1`, committed) reads each SVG, extracts every `<path d="…"/>` value, concatenates multiple `d` values with a space, and writes `ModernWigiDash.Widgets/GriddyIconPaths.g.cs`:
   - `public static partial class GriddyIconPaths` with `public static readonly IReadOnlyDictionary<string, string> Map` keyed case-insensitively (`StringComparer.OrdinalIgnoreCase`) by icon name (file name without `.svg`).
   - Any SVG that cannot be reduced to path data (non-`<path>` shapes such as `<circle>`/`<rect>`) is logged as a warning and skipped; the script fails if fewer than 1,000 icons resolve.
4. Commit `GriddyIconPaths.g.cs` + `LICENSE-GriddyIcons.txt`.

### 2. `GriddyIcons` (new, `ModernWigiDash.Widgets/GriddyIcons.cs`)

Public static class replacing `IconLibrary`:

- `IReadOnlyCollection<string> Names` → keys of the generated map.
- `bool Contains(string name)` → case-insensitive presence check.
- `bool TryGetPathData(string name, out string pathData)` → raw `d` string (trimmed).
- `bool TryGetPath(string name, out SKPath? path)` → parses via `SKPath.ParseSvgPathData(pathData)`; parses are cached in a `ConcurrentDictionary<string, SKPath>` (safe: the renderer is single-threaded). Returns false for blank/unknown names or if parsing throws.
- `void Draw(SKCanvas canvas, string name, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)`:
  - Look up the (cached) `SKPath`.
  - Compute scale = `sizePx / 24f`; build a transform matrix: translate the 24×24 viewBox so its center lands on `center + (offsetX, offsetY)`, scaled by `scale`.
  - `canvas.DrawPath(path, paint)` with `paint.Color = color`, `IsAntialias = true`. No-op when the icon is unknown.

### 3. `HotkeyButtonWidget.Render` (`UtilityAndInteractiveWidgets.cs`)

Replace the Material font-glyph block with a `GriddyIcons.Draw` call:

- `IconSize` (int, 0 = auto → 40% of min dimension), `IconOffsetX` (int), `IconOffsetY` (int), `IconColorHex` (string, `#FAFAFA`) keep their existing semantics (added in commit `9195476`).
- Default icon anchor: centered horizontally at `bounds.MidX`, vertical baseline at `bounds.Top + max(iconSize * 0.95, bounds.Height * 0.42)`, then offsets applied — same layout as today, just path-drawn.
- Blank or unresolvable `Icon` → no icon drawn; label layout unchanged.

### 4. Remove Material Symbols entirely

- Delete `ModernWigiDash.Widgets/Resources/Fonts/MaterialSymbolsRounded-Regular.ttf`.
- Delete `ModernWigiDash.Widgets/Resources/Fonts/LICENSE-MaterialSymbols.txt`.
- Delete `ModernWigiDash.Widgets/IconCodepoints.g.cs`.
- Delete `ModernWigiDash.Widgets/IconLibrary.cs`.
- Remove the `<None Include="Resources\Fonts\MaterialSymbolsRounded-Regular.ttf">` and `LICENSE-MaterialSymbols.txt` entries from `ModernWigiDash.Widgets/ModernWigiDash.Widgets.csproj`.
- Remove the `<Resource Include="..\ModernWigiDash.Widgets\Resources\Fonts\MaterialSymbolsRounded-Regular.ttf">` entry from `ModernWigiDash.App/ModernWigiDash.App.csproj`.
- Remove all `IconLibrary` references in `ModernWigiDash.App/MainWindow.xaml.cs` (icon picker branch and preview).

### 5. Searchable icon picker (`MainWindow.xaml.cs`, `WidgetPropertyType.Icon` branch)

Replace the Material combo + glyph preview with:

- A 22px preview: WPF `Path` inside a `Viewbox` with `Stretch=Uniform`, `Fill = white`. Data built from the selected icon's path data via `Geometry.Parse(pathData)` (SVG path mini-language is compatible with WPF's), wrapped in try/catch — blank preview on parse failure.
- A search `TextBox` (placeholder like "Search icons…"), filtering the full `GriddyIcons.Names` list case-insensitively (substring match) on every keystroke.
- A scrollable, virtualized `ListBox` (max height ≈ 200px) bound to the filtered names; selecting an item writes the name, updates `PropertyValues`, and refreshes the preview. Typing shows all names when the search box is empty.

### 6. Testing (offline)

Replace `IconLibrary_*` tests with:

- `GriddyIcons_AllPaths_ParseToSkPath` — every name in `GriddyIcons.Names` resolves via `TryGetPath` to a non-empty `SKPath` (bounds width/height > 0). Validates the entire generated dataset offline.
- `GriddyIcons_Names_CountAndUnique` — count ≥ 1000 and case-insensitively unique.
- `GriddyIcons_Unknown_ReturnsFalse` — blank/unknown names return false and a null/empty path.
- `HotkeyWidget_WithGriddyIcon_RendersWithoutExceptions` — `Icon = "activity"`, render, no exception.
- Keep `HotkeyWidget_IconPositionAndSize_DefaultToAutoCenter` and `HotkeyWidget_WithIconSizeAndOffsets_RendersWithoutExceptions` (update icon name to a griddy name).

## Error handling

- Unknown/blank icon name: no-op draw, picker can still select it (search only shows known names).
- Path parse failure at runtime: `TryGetPath` returns false, icon skipped; picker preview shows blank for that selection.
- Generation-time failures: script warns per-icon and aborts if the resolved set is too small.

## Success criteria

- `dotnet build ModernWigiDash.slnx` succeeds with zero errors/warnings.
- `dotnet test` passes (pre-existing lone Twitch WIP failure excluded) including the new griddy tests.
- No Material font/license/codepoints/`IconLibrary` artifacts remain in the repo or the built output.
- The icon picker lists 1,145 griddy names, filters as you type, and shows a live preview; Hotkey widgets render griddy icons with size/offset properties working.
