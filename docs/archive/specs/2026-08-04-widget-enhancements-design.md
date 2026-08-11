# Widget Enhancements: Text, FX, Hotkey Icons & Media Keys Design Specification

## Overview

This specification adds four widget features to `ModernWigiDash`:

1. A new **Text widget** that displays user-entered text with a selectable system font, color, size, and alignment.
2. **Currency exchange rate support** in the existing Stock & Crypto widget (`CryptoStockTickerWidget`) so users can track pairs like `EUR/USD`.
3. **Icon support for the Hotkey widget** — an open-source icon font is bundled with the app and exposed through a searchable icon picker; the chosen icon renders on the button.
4. **Dedicated media key actions** in the Hotkey widget's action editor, replacing the current "type the raw key name" flow with a friendly dropdown.

The work spans `ModernWigiDash.Sdk` (new property types), `ModernWigiDash.Core` (system-font lookup), `ModernWigiDash.Widgets` (new widget, feed extensions, icon library, editor catalogs), `ModernWigiDash.App` (inspector branches + action editor), and `ModernWigiDash.Tests`.

## Goals & Objectives

1. **Text widget**: Users can place a text box widget with text, an arbitrary installed font, color, size, and alignment; multi-line text wraps and centers correctly.
2. **FX pairs**: The Stock & Crypto widget tracks `XXX/YYY` currency pairs with live rates and change badges, reusing the existing Finnhub feed — no new API keys or network plumbing.
3. **Hotkey icons**: Bundled Apache-2.0 icon font renders glyphs on hotkey buttons; users pick from a searchable, previewed list of curated open-source icons, fully offline.
4. **Media keys**: Users select Play/Pause, Stop, Next, Previous, Volume Up, Volume Down, or Mute from a dropdown in the action editor instead of typing key names.
5. **No runtime network dependencies** added beyond existing feeds; no new NuGet packages.
6. **.NET 10 & C# best practices**: strict nullability, `using` disposal, pattern matching, SonarAnalyzer compliance — consistent with existing widget code.

## Architectural & Component Changes

### 1. Shared Building Blocks: New Property Types (`Font`, `Icon`)

Location: `ModernWigiDash.Sdk/Attributes.cs` and `ModernWigiDash.App/MainWindow.xaml.cs`

- Add two values to the `WidgetPropertyType` enum:
  - `Font`
  - `Icon`
- In `UpdateInspectorPanel` (the property-type `else if` chain starting around line 1048), add two branches:

**`Font` branch** — renders a searchable ComboBox listing every system font family:
- Add a `FontCatalog` static helper (in `ModernWigiDash.Core/Rendering`) that caches and sorts `SKFontManager.Default.FontFamilies` once.
- The inspector branch reads options the same way the existing `Choice` branch does: `IWidgetPropertyOptionsProvider.GetPropertyOptions` when the widget implements it, falling back to `attr.Options`; `TextLabelWidget` implements the provider and returns `FontCatalog` options for its `FontFamily` property.
- WPF `ComboBox` provides type-ahead search via `IsTextSearchEnabled = true`; no custom control needed.
- If the current value is not in the list (e.g., an uninstalled font), the stored value is still shown in the combo's text box so it is not silently lost.

**`Icon` branch** — renders a searchable ComboBox whose rows show a live glyph preview plus the icon name:
- Items source: `IconLibrary.Names` plus a leading `(None)` entry that clears the icon.
- Glyph preview: a `TextBlock` whose `FontFamily` points at the bundled icon font TTF via a `file://` URI (offline; no font installation required).
- Selected value stored as the icon name string.

### 2. `FontHelper` System-Font Lookup

Location: `ModernWigiDash.Core/Rendering/FontHelper.cs`

Today `GetTypeface(familyName, style)` ignores arbitrary family names and always returns the Geist typeface (only `Segoe UI Emoji` is special-cased). This makes the Text widget's "any system font" requirement impossible.

- Add `public static SKTypeface GetSystemTypeface(string familyName, SKFontStyle style)`:
  - Calls `SKTypeface.FromFamilyName(familyName, style)` first.
  - Falls back to Geist, then `Segoe UI`, then `SKTypeface.Default` when the family cannot be resolved.
- Route the existing family-aware `CreateFont(string familyName, ...)` overloads through this method so the Text widget and any future consumer get real system fonts while the Geist-primary behavior for callers that pass `"Geist"` is unchanged.
- Keep the emoji/symbol special-casing intact.

### 3. New Text Widget (`TextLabelWidget`)

Location: `ModernWigiDash.Widgets/TextLabelWidget.cs` (new file)

Metadata: id `text_label`, display name `Text`, category `Utilities`, default grid `Size2x1`.

Properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `Text` | `Text` | `"Your text here"` | Multi-line via `\n`; inspector TextBox accepts multiple lines |
| `FontFamily` | `Font` | `"Geist"` | Any installed system font |
| `FontSize` | `Number` | `32` | In points |
| `TextColorHex` | `Color` | `"#FAFAFA"` | |
| `Alignment` | `Choice` | `"Center"` | `Left` / `Center` / `Right` |
| `BackgroundHex` | `Color` | `"#00000000"` | Transparent by default; optional readability fill |

`Render` behavior:
- Build the font via the new system-font path (`GetSystemTypeface`), so the chosen family is honored with graceful fallback.
- Split `Text` on `\n`; word-wrap each line to `bounds.Width` using `SKFont.MeasureText`, breaking only between words (clip or break a single unbreakable word to avoid overflow).
- Block-vertical-center the entire paragraph within `bounds`; apply `Alignment` per line.
- Draw each line with `canvas.DrawTextWithFallback` so emoji/symbol glyphs still resolve via the existing per-run fallback.
- When `BackgroundHex` is non-transparent, draw a rounded-rect fill behind the text first.

Edge cases: empty text renders nothing; an unresolvable font falls back to Geist; over-long single words are broken rather than overflowing the bounds.

### 4. Currency Exchange in Stock & Crypto

#### 4a. `PriceFeedManager`

Location: `ModernWigiDash.Widgets/PriceFeedManager.cs`

- **FX detection**: a symbol matching `^([A-Za-z]{3})/([A-Za-z]{3})$` (e.g. `EUR/USD`) is classified as FX. The normalized key strips the slash and uppercases (`EURUSD`).
  - Add `public static bool IsFx(string symbol)` and extend `NormalizeSymbol`.
  - Refactor the asset-kind resolution used by `Subscribe` and `GetPrice` into a three-way decision (Crypto / Stock / FX) so `forceCrypto` semantics remain intact for existing callers.
- **Subscription**: FX symbols register in a new `_subscribedFx` set and are served by the existing Finnhub REST poller.
- **Poller**: in `RunStockRestPollerAsync`, FX symbols query the Finnhub forex endpoint `GET /quote?symbol=OANDA:EUR_USD&token=...`. `c` is the rate; `dp` (24h change %) may be absent in forex responses — a missing/null `dp` is treated as no change rather than an error.
- **`PriceInfo`**: add `public string CurrencySymbol { get; set; } = "$";` and update `FormattedPrice` to use it, enabling `€`, `£`, `¥`, etc. For FX the stored `Price` is the exchange rate; decimal precision is handled by the widget's existing `PriceDecimals`.

#### 4b. `CryptoStockTickerWidget`

Location: `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs`

- Add `"FX Pair"` to the `AssetType` Choice options (`Auto` / `Crypto` / `Stock` / `FX Pair`).
- Auto-detection: in `Auto` mode, an `XXX/YYY` pattern is classified as FX.
- Update the `Symbol` property description to mention FX pairs.
- Auto `DisplayName`: `EUR / USD` (spaces around the slash) when blank.
- Rendering: an FX asset uses the same layout as stock/crypto (pair label top, rate center, change badge bottom), with the rate formatted per `PriceDecimals` (4 decimals default for rates).

Edge cases: malformed pairs (e.g. `EURR/USD`) fall back to stock detection; empty symbols render the existing placeholder; temporarily unavailable forex data shows the same stale/placeholder state the widget already tolerates.

### 5. Hotkey Icons

#### 5a. `IconLibrary`

Location: `ModernWigiDash.Widgets/IconLibrary.cs` (new file)

- Loads the bundled **Material Symbols Rounded** font (Apache-2.0) from `Resources/Fonts/` using the same base-directory/assembly-location resolution pattern `FontHelper` uses.
- Holds a curated `name → codepoint` dictionary (~200 common icons: power, apps, browser, music, volume, camera, settings, etc.), generated from the icon set's codepoints file and checked in as source.
- API:
  - `public static IReadOnlyList<string> Names`
  - `public static int GetCodepoint(string name)`
  - `public static string GlyphString(string name)` — returns the UTF-32 codepoint as a string for WPF preview and Skia drawing.
  - `public static SKTypeface? GetTypeface()` — lazily loaded.

#### 5b. `HotkeyButtonWidget`

Location: `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs`

New properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `Icon` | `Icon` | `""` | Empty = no icon |
| `IconColorHex` | `Color` | `"#FAFAFA"` | Icon tint |

`Render` behavior: when `Icon` is set, draw the glyph via `IconLibrary.GetTypeface()` centered above the label at ~40% of `min(bounds.Width, bounds.Height)`, with the label below. Existing layout is unchanged when no icon is set.

#### 5c. Packaging

- Material Symbols Rounded TTF in `ModernWigiDash.Widgets/Resources/Fonts/` with `CopyToOutputDirectory = PreserveNewest` so it lands in the shared output `Resources/Fonts/` folder next to Geist; its license file ships alongside.
- No runtime network access; icons render offline.

### 6. Hotkey Media Keys

#### 6a. `MediaKeyCatalog`

Location: `ModernWigiDash.Widgets/MediaKeyCatalog.cs` (new file)

- Shared mapping of friendly display names to executor key values:

  | Display | Value |
  |---|---|
  | Play/Pause | `PLAYPAUSE` |
  | Stop | `STOP` |
  | Next Track | `NEXT` |
  | Previous Track | `PREVIOUS` |
  | Volume Up | `VOLUMEUP` |
  | Volume Down | `VOLUMEDOWN` |
  | Mute | `MUTE` |

- API: `public static IReadOnlyList<WidgetPropertyOption> Options` and `public static string? FriendlyName(string value)`.

#### 6b. Executor fix

Location: `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs`, `HotkeyActionExecutor.ParseVirtualKey`

- Add `"STOP" => 0xB2` (`VK_MEDIA_STOP`) — currently missing, so Stop does not work today.

#### 6c. Action editor

Location: `ModernWigiDash.App/MainWindow.xaml.cs`, `ShowHotkeyActionEditor`

- When the selected `Action type` is `MediaKey`, the Value row swaps from a TextBox to a ComboBox bound to `MediaKeyCatalog.Options`.
- Switching kinds converts an existing value both ways: stored executor value ⇄ friendly display name (e.g. `VOLUMEUP` ⇄ "Volume Up").
- Saving a `MediaKey` action writes the catalog's stored value to `HotkeyAction.Value`, so serialization and the executor are unchanged.
- The save validation requires a catalog value for `MediaKey` actions.

#### 6d. `HotkeyAction.Summary()`

- `MediaKey` returns the friendly label, e.g. `Media: Play/Pause`, instead of `MediaKey: PLAYPAUSE`.

## Verification Plan

1. **Automated Unit Tests** (added to `ModernWigiDash.Tests/UnitTestSuite.cs` following existing patterns — render-without-exception, default-hex, and static/parsing checks that avoid network calls and real input):
   - `TextLabelWidget` defaults (color/font/size); renders with a custom font, custom color, multi-line text, and an invalid font family (must fall back without throwing).
   - `PriceFeedManager` FX detection/normalization of `EUR/USD`-style symbols; non-FX symbols still classify as stock/crypto.
   - `HotkeyAction.Summary()` returns the friendly label for `MediaKey`.
   - `MediaKeyCatalog` — every entry maps to a valid executor key name.
   - Default-hex assertions for the new color properties (`TextLabelWidget.TextColorHex`, `HotkeyButtonWidget.IconColorHex`).
2. **Build**: `dotnet build ModernWigiDash.slnx` compiles cleanly.
3. **Manual verification**:
   - Add a Text widget; set multi-line text, switch fonts, colors, and alignment; confirm rendering on the dashboard surface.
   - Add a Stock & Crypto widget with `EUR/USD`; confirm the rate and change badge update.
   - Add a Hotkey widget; pick an icon and confirm the glyph renders with the chosen tint; add a `MediaKey` action and confirm the dropdown shows friendly names and playback/volume input fires.
   - Confirm saved profiles round-trip the new properties (icon name, font family, FX symbol).
