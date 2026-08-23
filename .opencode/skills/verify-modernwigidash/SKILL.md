---
name: verify-modernwigidash
description: "Drive the ModernWigiDash WPF app the way a user does and prove behavior with UIA evidence (launch, doctor, drive, capture, cleanup). Use when a change touches the WPF UI, the widget canvas, the inspector, the page tabs, the theme, or profile import/export and you need a scripted proof on top of the unit tests. Physical-display behavior (USB, standby, frames on the LCD) routes to the hardware-e2e-validation skill instead."
---

# verify-modernwigidash

Scripted proof for the WPF app: launch a disposable instance, drive it through UIA the way a user would, and capture evidence that survives cleanup. Unit tests cover policy through seams; this skill covers the app's real user surface at 1:1 scale.

Harness: `scripts/wmd-verify.ps1`, one PowerShell 5.1 invocation per action, repo-root-relative invocation works from anywhere:

```
powershell <root>\.opencode\skills\verify-modernwigidash\scripts\wmd-verify.ps1 <command> [args]
```

> **Why the harness is a hand-rolled `ComImport` bridge (not `New-Object -ComObject`):** it drives **UIA v4 core** (`UIAutomationCore`), not the legacy v3 managed client. Two independent reasons it can't be a plain COM call: v4 objects are **vtable-dispatched (no `IDispatch`)**, inherent to the v4 API, so PowerShell can never late-bind them, and this Windows build registers the coclass as a **bare CLSID (no ProgID/typelib)**, so it can't be auto-instantiated or have a generated interop either. The typed-vtable interop is compiled via `Add-Type`; the full three-constraint rationale is the comment block at the top of `wmd-verify.ps1`.

## Preconditions (baseline state)

- A Release build exists: `dotnet build ModernWigiDash.slnx -c Release --nologo` (the harness finds the newest `ModernWigiDash.App\bin\Release\*\ModernWigiDash.App.exe`).
- No other ModernWigiDash instance is running. The harness enforces this on `launch` and `doctor` (shared-instance rule: two instances share `profile.json`).
- The session is an interactive desktop (UIA needs a visible window). A non-elevated shell is correct here: without a USB display the engine settles to `Simulated` and the whole UI is drivable. Elevation is only for physical-display runs (see the Out of scope rule below).
- Evidence dir for the run: `%TEMP%\opencode\wmd-verify-evidence\<run-slug>\` (create it first). Every artifact goes there with the feature id in the path.
- Run `backup-profile` before any feature that mutates the profile (pages, widgets, import). `clean` restores it.

## Launch

```
powershell .opencode\skills\verify-modernwigidash\scripts\wmd-verify.ps1 launch
```

Starts the newest Release build unelevated, records the pid in `%TEMP%\opencode\wmd-verify.state.json`, and blocks until the `ModernWigiDash` window is UIA-visible (30 s budget) or fails. On failure it already stopped its own process and dropped the state.

Teardown is `clean`, never ad hoc (it stops only the recorded pid, restores the profile, drops the state):

```
powershell .opencode\skills\verify-modernwigidash\scripts\wmd-verify.ps1 clean
```

## Doctor

```
powershell ... wmd-verify.ps1 doctor                 # main window health
powershell ... wmd-verify.ps1 doctor -Window "Theme" # a modal dialog's health
```

Exit 0 means: the launched pid is alive, no second instance exists, the window is UIA-visible and enabled. Run it before the first drive of a feature and after any failed drive. A doctor failure caused by this skill's own drift (a renamed handle, a new startup dialog) is drift: fix the skill or the feature map, relaunch, retry once, then call the run blocked.

## Drive

The feature recipes in `features/` pair every user action with one `wmd-verify.ps1` command and the observable result that proves it. Stable handles (case-insensitive contains match on Name **or** AutomationId; WPF maps `x:Name` to AutomationId, and string button content to Name, so the glyph icons below are findable by their glyph):

| Handle | Surface |
|---|---|
| `Theme` (id `BtnTheme`, name `🎨 Theme`) | theme dialog opener (top bar) |
| `Theme Customization` (window title `🎨 Theme Customization`) | theme dialog |
| `AddPage` (id `BtnAddPage`, name `+ Add Page`) | add page |
| `📄 <PageName>` (tab button name) | page tab |
| `✏️` (tab rename button, name = glyph) | rename (repeated, `list` + `click-nth`) |
| `✕` (tab close button, name = glyph) | delete page (repeated, `list` + `click-nth`) |
| `Rename Page` (window title, fixed) | rename prompt, input is a nameless `[Edit]` → `set-in "Rename Page" "<value>"` |
| `Delete Page` (window title) | page-delete confirm dialog (body names the page + widget count) |
| `ExportProfile` (`💾 Export Profile`), `ImportProfile`, `ClearCanvas` | status bar actions |
| `ActiveCount` (id `TxtActiveCount`, value `Active Widgets: N`) | status bar count |
| `SearchCatalog` (id `TxtSearchCatalog`) | catalog filter box |
| `<pluginId>` (catalog row `ListItem`, Name = `PluginInfo { ... }` ToString, e.g. `weather_forecast`) | catalog row identity node, read-only (`find`/`list` for filter/count/proof) |
| `BtnPlace_<pluginId>` (catalog place button, unique AutomationId, name `+ Place on Canvas`) | place on the active page, Invoke, realized off-screen, headless-safe. Never use a display-name needle: it collides with the `📄 <PageName>` tab button |
| `Remove Widget from Canvas` | inspector delete (Invoke, visibility-independent; reported bounds may sit off-window when scrolled) |
| (canvas, no UIA peer: `SkiaCanvas`/`PreviewFrame` match nothing) | the 1016×592 composited preview → canvas pointing via `click-screen <x> <y>` (absolute screen coords) |
| `SnapToGrid`, `EditMode` | top-bar checkboxes |
| `UsbStatus` (id `TxtUsbStatus`) | USB badge text |

Repeated handles (the per-tab `✕`/`✏️`) need the lookup trio instead of a bare `click`: `list <needle> [buttons]` (read-only, numbered matches in tree order, left-to-right within each container, with type + position; scoped to the launched app's pid so foreign windows, e.g. the shell, can never pollute the numbering), then `click <needle>` (first match, Invoke) or `click-nth <needle> <n>` (Nth **button** match, Invoke). `list` first, click second, never guess N. Dialog windows with a nameless input (the themed prompts) take `set-in <windowTitle> <value>`: it writes the window's first writable text control and prints the read-back, **commit the dialog (`click OK`) only after the read-back matches the intended value**; otherwise `click Cancel`.

Driving conventions:

- Start every recipe from the baseline state unless its preconditions say otherwise.
- Prefer the AutomationId handles above over coordinates. The only mouse-backed step is canvas pointing (`click-screen <x> <y>`, absolute screen coords. The preview canvas has no UIA peer).
- Canvas pointing (`click-screen <x> <y>`) needs the synthetic mouse. In headless/agent sessions the cursor cannot be placed, so the harness's mouse fallback refuses with a clear error instead of clicking the physical cursor position. That step is precondition-blocked there: mark it unreachable (with the precondition named), do not improvise. Everything else runs through Invoke, including the catalog place step (`click "BtnPlace_<pluginId>"`).
- Treat every command as literal; keep quoted names unchanged.
- Modal dialogs: drive them before returning to the main window; `doctor -Window <part>` and `wait <part>` are the health/wait pair. Stacked dialogs of the same kind share coordinates, probe (`find <title>`) to count them and cancel top-down.
- Wait for observable results, not fixed sleeps (a `value` read until it changes beats a `Start-Sleep`).
- The WPF preview canvas is Skia-drawn and exposes NO UIA peer at all (neither `SkiaCanvas` nor `PreviewFrame` matches in the tree, not even the bounds). Canvas pointing goes through `click-screen <x> <y>` (absolute screen coords), and what was drawn is proven by `shot`.
- **Empty-string args drop in transit**: PowerShell 5.1 drops empty-string args when marshaling to native commands, so `set <box> ""` reaches the harness with one arg and fails with `usage: set <needle> <value>`. Route through cmd to preserve the empty value: `cmd /c 'powershell -NoProfile -File <harness> set <needle> ""'` (verified 2026-08-21 on the catalog filter reset: 12→1→12). The same trap hits any empty-string arg (`set-in` included).

## Evidence

Proof artifacts live in the run's evidence dir and survive `clean` (cleanup never deletes them):

- **UI proof** = the action's command output **plus** a `shot` PNG taken after the resulting state, plus a `dump`/`find` read of the UIA state before and after when the claim is structural. A screenshot alone is only half a proof: it shows the state, not that the action produced it.
- **Mutation proof** = a read-only second view of the stored value: the profile JSON on disk after the UI mutation, or the counter text re-read after the action.
- **Negative proof** = the command that failed or the `wait` that timed out, with the unmet precondition named.
- Name artifacts `<feature-id>/<action>.png` (`.txt` for dumps). Record which feature file and which entry point each artifact comes from.
- Report an unreachable path with the attempted commands and the unmet precondition. Never report a skipped entry point as verified through a different path.

## Cleanup

1. Roll back the feature's profile mutations (`restore-profile` covers profile.json and app_theme.json when they were backed up; drive undo paths first when the feature has them).
2. Run `clean`. Confirm the evidence dir still exists complete.
3. Never kill by process name. The harness kills only the recorded pid; if a driver step spawned extra processes (it shouldn't), list them for the user instead of sweeping.

## Out of scope

- **The physical display.** USB connect, frames on the LCD, standby, and any behavior only visible on the WigiDash route to the `hardware-e2e-validation` skill (elevated, real device, its own helper scripts under `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\`). The `usb-status` feature below proves the UI side (the badge state machine in Simulated mode) only.
- Anything a unit test already pins through a seam (presentation math, policy modules, protocol framing). This skill is for the user surface the tests cannot reach.
- Pixel QA of widget rendering beyond capture: `shot` proves a state, a pixel-diff harness proves a parity claim (the poteto-mode Visual parity playbook).

## Feature map

`features/` is the maintained source for user-facing behavior. Read `features/README.md` before driving, then use the matching feature file as the recipe. New user-facing surfaces get a feature file before their first proof that uses them (the map is the repo's verification source, not disposable scaffolding).