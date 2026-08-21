# Place a widget

The catalog (left column) lists every registered widget; each row's `+ Place on Canvas` button binds it to the current page. The preview canvas (center, 1016×592 at 1:1) repaints, the status bar counts active widgets on the active page, and selecting a widget opens the right-column inspector.

## Sub-features

- `place-open` filters the catalog to one widget via the search box.
- `place` places the widget on the active page; the Active Widgets count increments and the canvas repaints.
- `inspect` selects the placed widget on the canvas; the inspector shows its name and transform fields.
- `place-delete` removes the selected widget through the inspector's Remove button; the count decrements.

## How to get to it (user POV)

- Type in the catalog search box, then choose the row's `+ Place on Canvas` button.
- Click the widget on the canvas to select it.
- Choose `🗑️ Remove Widget from Canvas` in the inspector (or press `Delete` with the widget selected and no text box focused).

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor`; `backup-profile` ran.
- Baseline: `value ActiveCount` (counts the ACTIVE page — read the actual value, do not assume).
- Only the canvas select step needs the synthetic mouse (the cursor must be placeable; the harness verifies `SetCursorPos` before clicking). In a headless session that step is precondition-blocked: mark it unreachable (with the precondition named), do not improvise. Filter, place, and delete all run through Invoke.

**Catalog row structure (UIA reality — drives every command below):** the catalog is a `ListBox` with virtualization off (every row container and its button is realized even when scrolled off-screen — the automation surface is deliberately stable). Each row exposes two nodes:

- The row node: a `ListItem` whose Name is the row's `PluginInfo { PluginId = ..., DisplayName = ..., ... }` ToString. The row's needle is the **PluginId** (e.g. `weather_forecast`): unique, and it never collides with the `📄 <PageName>` tab buttons. A display-name needle (`Weather Forecast`) ALSO matches the page tab when a page is named after the widget — `click`/`click-nth` on it clicks the TAB (a page switch, not a placement).
- The place button: a `Button` with a globally unique AutomationId `BtnPlace_<pluginId>` (e.g. `BtnPlace_weather_forecast`) and Name `+ Place on Canvas`. This is the placement handle — `click "BtnPlace_<pluginId>"` (Invoke, visibility-independent). The id can never collide with a tab or another row.

- **Filter.** Type a widget name into the catalog search. Run `set SearchCatalog Weather`. Proof: `list "BtnPlace_" buttons` reports exactly one button (before filtering it reports 12 — record both counts).
- **Place.** Run `click "BtnPlace_weather_forecast"` (Invoke — the unique id needs no filter, works off-screen, headless-safe). Proof (counter): `value ActiveCount` incremented by exactly one on the SAME active page. Proof (visual): `shot <evidence>/place/after-place.png`. A +2 jump means two click sources fired at once (a concurrent manual click is the usual cause) — re-read the counter and the on-disk profile before proceeding; a single Invoke places exactly one widget (verified: two fresh launches, first action = Invoke, each exactly +1).
- **Select on canvas.** The preview surface exposes NO UIA peer at all (`find SkiaCanvas` and `find PreviewFrame` both report no match — neither name exists in the tree), so canvas pointing takes absolute screen coordinates: `click-screen <x> <y>` at the preview center. On the default 1680×900 window at @(1080,606): (1900, 1065) — verified. For another window size, derive x from the 280px catalog / 320px inspector side columns and y between the `LIVE WIGIDASH PREVIEW` header and the page-tab strip (both findable). A full-size widget (weather's 1016×592 default) covers the whole preview, so a center click selects it. Proof: `find "Remove Widget from Canvas"` hits one button (the inspector exited its empty state).
- **Delete.** Choose `🗑️ Remove Widget from Canvas`. Run `click "Remove Widget from Canvas"` (Invoke — visibility-independent). Proof (counter): `value ActiveCount` returned to the baseline. Proof (empty state): `find "No Widget Selected"` hits the inspector again.
- **Evidence.** Save the three `value ActiveCount` outputs, the canvas before/after shots, and the inspector `find` outputs as `<evidence>/place/<step>.txt|png`.

## Gotchas

- `Set SearchCatalog` filters live: an empty string restores the full list. Never leave a stale filter; it changes what later `find` calls see.
- **Display-name needles collide with page tabs.** A page named after a widget (e.g. `📄 Weather Forecast`) makes `click`/`click-nth "Weather Forecast"` click the tab: the active page switches and `ActiveCount` JUMPS to that page's count instead of incrementing by one. Use the `BtnPlace_<pluginId>` id (or the PluginId row needle) for the catalog; a jump instead of +1 is the tell that a page switched.
- **`ActiveCount` is per-active-page, not global.** Placing a widget on page A never changes page B's count.
- **The canvas has no UIA peer; the place button does.** Canvas pointing is `click-screen <x> <y>` (absolute screen); placement is Invoke on the unique `BtnPlace_<pluginId>` id. If a drive "places" a widget but the count jumped, you clicked a tab. The counter is the arbiter.
- **The Remove button's reported bounds can sit outside the window** (it scrolls out of the inspector's ScrollViewer; WPF reports unclipped content coordinates — observed @y=1999 in a 1506-bottom window). `click` uses Invoke and is unaffected. Never `click-screen` at those reported coordinates.
- Widget placement position comes from the widget's `DefaultGridSize` metadata (or the page layout), not from the click — the proof is the snapshot, not a coordinate assertion.
- Pressing `Delete` with a text box focused edits the field, not the widget (a deliberate window policy). When proving keyboard delete, verify no editor has focus first; simpler to always use the Remove button.
- Placing a second widget of the same type is legal; the counter is the arbiter, and the snapshot must show two glyphs.
- Some widgets render async state on tick (clocks redraw every frame, hardware widgets show placeholders without the services): take the snapshot a beat after the repaint, and expect placeholder text where a live source is absent — that is the correct Simulated-mode rendering, not a failure.