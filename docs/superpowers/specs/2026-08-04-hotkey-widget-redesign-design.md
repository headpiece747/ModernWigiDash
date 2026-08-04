# Hotkey Widget Redesign — Design Spec

**Date:** 2026-08-04
**Status:** Approved design (pending spec review)
**Scope:** `ModernWigiDash.Widgets` + `ModernWigiDash.App` + `ModernWigiDash.Tests`

## Overview

Redesign the Hotkey widget from a multi-action, toggle-capable button into a simple
single-action button. Remove the toggle machinery and both action-list editors, replace
the Task Manager action with 7 working media-key actions, and replace the current
icon-picker editor with an icon-name box + Browse... button opening a popup that offers
both the bundled Griddy icon set and single-path SVG files.

## Design decisions (approved)

| Decision | Choice |
|---|---|
| Actions per button | Exactly one action (Launch App / Open URL / one media key) |
| Action storage | Flattened scalar properties; reuse existing `ActionType` + `ActionCommand` |
| Icon sources | Griddy icon name OR a copied single-path SVG file |
| Raster images | Out of scope (PNG/JPG/BMP/GIF not supported) |
| SVG renderer | Single-path extraction via `SKPath.ParseSvgPathData` — no new dependency |
| SVG tinting | Custom SVGs ARE tinted by `IconColorHex` (plain geometry) |
| Custom file persistence | Copy into `%LocalAppData%\ModernWigiDash\icons\`, store relative path |
| Inspector layout | Single column, one field per row, inside the fixed 320px panel |

## Data model

`HotkeyButtonWidget` (`ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs:282`).

### Kept unchanged (10 properties)

- `ButtonLabel` (`:288`)
- `Description` (`:291`)
- `ButtonColorHex` (`:300`)
- `TextColorHex` (`:303`)
- `Icon` (`:306`) — Griddy icon name, blank = none
- `IconColorHex` (`:309`)
- `IconSize` (`:312`)
- `IconOffsetX` (`:315`)
- `IconOffsetY` (`:318`)
- `ActionCommand` (`:297`)

### Changed (1 property)

- `ActionType` (`:294`) — choice list changes from
  `["Launch App", "Open URL", "Task Manager"]` to:
  `["Launch App", "Open URL", "Media Play / Pause", "Media Next", "Media Previous", "Media Stop", "Volume Up", "Volume Down", "Mute"]`.

### Added (1 property)

- `IconFile` (`string`, `WidgetPropertyType.Path`) — relative reference to a copied
  SVG file (e.g. `icons/power-logo.svg`). When set, it **wins** over `Icon`; the
  Griddy name is ignored until the file is cleared.

### Removed (4 properties + runtime state)

- `ToggleActions` (`:321`)
- `ToggledButtonLabel` (`:324`)
- `Actions` (`List<HotkeyAction>`) (`:327`)
- `ToggledActions` (`List<HotkeyAction>`) (`:330`)
- Runtime: `_isToggled`, the `ToggleActions` branch of `ExecuteActionsAsync`
  (`:429`), and the "if list empty use legacy action" fallback (`:430-434`).

## Action execution

`OnTouch` (`:406`) builds exactly one `HotkeyAction` via a mapping helper and calls
`HotkeyActionExecutor.ExecuteAsync([action], token)`.

`ActionType` → action mapping (replaces/extracts `CreateLegacyAction`, `:452`):

| ActionType | Kind | Value |
|---|---|---|
| `Launch App` | `Launch` | `ActionCommand` |
| `Open URL` | `OpenUrl` | `ActionCommand` |
| `Media Play / Pause` | `MediaKey` | `PLAYPAUSE` |
| `Media Next` | `MediaKey` | `NEXT` |
| `Media Previous` | `MediaKey` | `PREVIOUS` |
| `Media Stop` | `MediaKey` | `STOP` |
| `Volume Up` | `MediaKey` | `VOLUMEUP` |
| `Volume Down` | `MediaKey` | `VOLUMEDOWN` |
| `Mute` | `MediaKey` | `MUTE` |
| `Task Manager` (legacy profile) | `Launch` | `taskmgr.exe` |

- Media key values come from a fixed map aligned with `MediaKeyCatalog`; the existing
  `HotkeyActionExecutor.ParseVirtualKey` media VK codes (`0xB0`-`0xB3`, `0xAD`-`0xAF`)
  are reused unchanged.
- `HotkeyAction` and `HotkeyActionExecutor` classes remain (executor needs them; tests
  cover `ParseVirtualKey` and `HotkeyAction.Summary`).

## Icon rendering

`HotkeyButtonWidget.Render` (`:338`):

1. `IconFile` set and resolves to an existing copied SVG → render via single-path SVG
   extraction, scaled to `IconSize` (0 = auto ~40% of min dimension), centered with
   `IconOffsetX/Y`, tinted by `IconColorHex`.
2. Else `Icon` is a valid Griddy name → existing `GriddyIcons.Draw` path with
   `IconColorHex` tinting.
3. Else → `DrawLabelOnly`.

### Single-path SVG extraction (no new dependency)

- Parse the SVG file as XML, extract the first `<path>` element's `d` attribute, feed
  it to the same `SKPath.ParseSvgPathData` used by Griddy (`GriddyIcons.cs:40`).
- Cache parsed `SKPath`s keyed by file path (mirrors `GriddyIcons.PathCache`, `:12`).
- Documented limitation: multi-shape / gradient / stroke-based SVGs render wrong or
  empty; the picker warns "Only single-path SVG icons are supported" for files with
  more than one path.

## Copy-on-select

When the user picks an SVG via the file dialog:

- Copy the file into `%LocalAppData%\ModernWigiDash\icons\` with a unique filename.
- Store only the relative path (`icons\<file>.svg`) in `IconFile`.
- Profiles export/import cleanly because the file ships with the icons dir.

## Inspector UI

Single-column, one field per row, inside the fixed 320px panel (`MainWindow.xaml:69`,
scrollable via `:152`). No sidebar widening.

Fields (in order): Button Label, Description, Action Type (dropdown), Action / Command,
Button Color, Text Color, **Icon (box + Browse... button)**, Icon Color, Icon Size,
Icon Offset X, Icon Offset Y.

- The `Action / Command` field shows only for `Launch App` / `Open URL`; hidden or
  disabled for media options (pattern mirrors the existing media-visibility swap,
  `MainWindow.xaml.cs:1410-1419`).
- Removed from inspector: Task Manager choice, Toggle Actions, Toggled Button Label,
  Actions + "edit actions", Toggled Actions + "edit toggled actions".
- The `ActionList` inspector branch (`MainWindow.xaml.cs:1174-1185`) and
  `ShowHotkeyActionEditor` dialog (`MainWindow.xaml.cs:1344`) are deleted.
- The existing icon-picker editor (`MainWindow.xaml.cs:1097-1155`) is replaced.

### Icon Selector popup (Browse...)

- Search box filtering the 1,159 Griddy icons live.
- **Browse SVG...** button → file dialog filtered to `*.svg`; on selection, validates
  single-path, copies into `icons\`, sets `IconFile`.
- Grid of Griddy icons; click to select (amber highlight).
- Footer: selected icon name + **Select** button to apply.
- A chip shows the currently-chosen custom file (e.g. `power-logo.svg`).
- Empty search → "No icons found" empty state.

## Error handling

- Empty `ActionCommand` on Launch App / Open URL → log error, no-op (guard style
  matches today's `CreateLegacyAction`; exceptions from `ExecuteActionsAsync` are
  already caught and logged at `:439-443`).
- Legacy `"Task Manager"` `ActionType` loaded from an old profile → maps to
  `Launch`/`taskmgr.exe`.
- `IconFile` set but file missing → label-only render, log "missing custom icon file"
  (no silent fallback to Griddy `Icon`).
- Multi-path / unparseable SVG → warning logged, label-only render.
- Copy-on-select failure (permissions, disk) → message box, `IconFile` unchanged.
- Both `Icon` and `IconFile` set → file wins.

## Profile migration

Old profiles carrying `Actions`/`ToggledActions`/`ToggleActions` still load — the
removed properties are ignored by JSON deserialization; the single action is derived
from `ActionType`/`ActionCommand` (with the Task Manager fallback). No profile file
rewriting required.

## Testing

Update `ModernWigiDash.Tests/UnitTestSuite.cs` (suite currently 61 pass / 1 known WIP
fail on the Twitch font test owned by the user's uncommitted `SocialAndVisualWidgets.cs`
— never staged; verification requires that 1 known failure stays the only failure).

Kept as-is (still valid): the existing hotkey icon/size/offset tests
(`:196-236`), `MediaKeyCatalog_ListsSevenActionsWithFriendlyNames` (`:238`),
`HotkeyAction_MediaKeySummary_UsesFriendlyName` (`:249`),
`ParseVirtualKey_MediaKeys_IncludeStop` (`:257`).

New tests:

1. `HotkeyWidget_ActionType_DefaultsToLaunchApp` — `ActionType == "Launch App"`,
   `ActionCommand == ""`.
2. `HotkeyWidget_MediaActionTypes_MapToMediaKeys` — each of the 7 media `ActionType`
   values maps to the correct `MediaKeyCatalog` value via the shared mapping helper.
3. `HotkeyWidget_TaskManagerLegacyType_MapsToLaunchTaskmgr` — `ActionType="Task Manager"`
   produces `Launch`/`taskmgr.exe`.
4. `HotkeyWidget_OpenUrlActionType_MapsToOpenUrl`.
5. `HotkeyWidget_CustomSvg_ExtractsSinglePathAndRenders` — write a small single-path
   SVG to temp, set `IconFile` (absolute, bypassing copy), `Render` without exceptions,
   assert parsed path non-empty.
6. `HotkeyWidget_CustomSvg_MultiPath_FallsBackToLabelOnly` — temp SVG with 2 `<path>`s
   renders without exceptions.
7. `HotkeyWidget_CustomSvg_MissingFile_FallsBackToLabelOnly` — nonexistent `IconFile`
   renders without exceptions.
8. `HotkeyWidget_IconFile_WinsOverIcon` — both set → renders, uses the file path.
9. `HotkeyWidget_SingleAction_ExecutesOneAction` — build the widget's action via the
   mapping helper; assert kind/value for Launch, Open URL, and one media key (no live
   SendInput — the helper returns the `HotkeyAction`, not the executor call).

Verification: `dotnet test ModernWigiDash.slnx` must pass all but the single known WIP
failure.

## Out of scope

- Raster image icons (PNG/JPG/BMP/GIF).
- Full-document SVG rendering (multi-shape, gradients, strokes).
- Macro / multi-action sequences.
- Toggle behavior and toggled states.
- Refactoring of the inspector grid or other widgets.
