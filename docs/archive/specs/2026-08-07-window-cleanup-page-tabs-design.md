> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`). Archived for history.

# Design: Window cleanup, page tabs under canvas, keyboard delete

**Date:** 2026-08-07
**Status:** Approved
**Scope:** `ModernWigiDash.App` WPF chrome only (MainWindow.xaml + MainWindow.xaml.cs). No Core/Widgets/Service changes.

## Goal

Clean up the app window's marketing text, relocate the page-tab UI from the top header to directly under the preview canvas, and let the Delete/Back key remove the selected widget.

## Changes

### 1. Text removals (XAML only)

| Location | Change |
|---|---|
| `MainWindow.xaml:8` | `Title="ModernWigiDash"` — drop `" - Next-Generation .NET 10"` |
| `MainWindow.xaml:42` | Subtitle becomes `"Free-Form Canvas"` — drop `"& Dynamic Engine"` |
| `MainWindow.xaml:137` | Preview header becomes `"🖥️ LIVE WIGIDASH PREVIEW (1016 x 592)"` — drop `"• 30 FPS"` |
| `MainWindow.xaml:262` | Delete the `TxtStatusTelemetry` TextBlock (`.NET 10 \| Rendering: 30 FPS \| Canvas: 1016 x 592`). Static text; no code-behind references it (verified). |

### 2. Page tabs move under the canvas

- Remove the header's center section (currently `MainWindow.xaml` lines 47–60: the `ScrollerPageTabs`/`PanelPageTabs` ScrollViewer + `BtnAddPage`). The header grid keeps its three columns (`Auto/*/Auto`); the middle column simply becomes an empty spacer, leaving logo left, toggles right unchanged.
- The center column (currently a two-row grid: 36px header + canvas) gains a third row of height `Auto` (~40px) at the bottom, holding the same tab strip: `ScrollerPageTabs` (with `PanelPageTabs`) + `BtnAddPage`, horizontally centered under the 1016×592 canvas.
- Element names are unchanged, so `BtnAddPage_Click`, `ScrollerPageTabs_MouseWheel`, and all page-refresh code operate without edits.

### 3. Keyboard delete (shared path + PreviewKeyDown)

- Extract `DeleteSelectedWidget()` from `BtnDeleteWidget_Click` (`MainWindow.xaml.cs:760`): removes `_selectedWidget` from `_profile.ActivePage.Widgets`, calls `SelectWidget(null)`, `UpdateActiveCount()`, `SkiaCanvas.InvalidateVisual()`. The button handler becomes a one-line call to it.
- Add a window-level `PreviewKeyDown` handler: on **Delete** or **Back**, when `Keyboard.FocusedElement` is not a `TextBox`, call `DeleteSelectedWidget()`.
- No selection → no-op (mirrors the button).

### 4. Edge cases

- Backspace/Delete while focused in an inspector `TextBox` (X/Y/Width/Height): guarded — never deletes.
- No widget selected: no-op.
- Deleting the last widget on a page: page remains (existing behavior — no page cleanup).
- Tab strip keeps horizontal mouse-wheel scrolling (existing handler).

## Out of scope

- The inspector's existing "🗑️ Remove Widget from Canvas" button stays as-is.
- No changes to `ProfileOps`, widget placement, or profile serialization.

## Testing

- `dotnet build ModernWigiDash.slnx -c Release` — 0 errors.
- Full test suite (295 tests) — the delete path funnels through existing profile-page removal covered by `ProfileOpsTests`; this change is XAML glue plus one thin handler, no new unit tests.
- Live verification (restart service + app): layout renders correctly (tabs under canvas, header clean); selecting a widget then pressing Delete removes it; typing in an inspector field then pressing Backspace does not delete.
