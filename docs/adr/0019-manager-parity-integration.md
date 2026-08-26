# ADR-0019: Manager parity: HKCU autostart, global hotkeys with a machine-local kill switch, and user-supplied AHK scripts

**Date:** 2026-08-26
**Status:** Accepted
**Deciders:** Project owner

## Context

The user is replacing the vendor WigiDash Manager, whose settings surface adds
two app-level options ModernWigiDash lacked ("Start with Windows", and an AHK
integration whose global-hotkey action is "run a user-picked .ahk script"),
plus two display tweaks (clock seconds, weather location hide). Vendor facts
extracted from the installed Manager (`C:\Program Files (x86)\G.SKILL\WigiDash
Manager`): the autostart is a plain `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
entry; the "AHK integration" is an embedded AutoHotkey v1 engine
(`AutoHotkey.Interop.dll`) running inside an x86 Windows service, with global
hotkeys that execute the user-picked scripts.

ModernWigiDash is 64-bit and service-less (ADR-0005): the app owns the USB
device directly and there is no service to host an embedded interpreter
engine, so the AHK half is reshaped rather than ported. Global hotkeys are
also exactly the OS-visible behavior that game anti-cheat flags: a
system-wide listener plus a spawned interpreter process can read as a cheat
tool, so the integration needs one off switch.

## Decision

### Start with Windows (HKCU, per-user, no elevation)

The settings hub's BEHAVIOR group "Start with Windows" checkbox
writes/deletes the one `Run` entry the app owns,
`ModernWigiDash = "<currently running exe>" --startup` (`AutostartPolicy`,
App). The registry is the single source of truth: the checkbox reads the
entry's presence and the toggle commits through `IAutostartStore` with no
Apply step, so the choice is machine-local by construction and a profile
import can never overwrite it. HKCU, not HKLM: per-user autostart without
elevation (the vendor-verified shape; no HKLM, a deliberate non-goal). The
command line carries the `--startup` flag (`StartupLaunchPolicy`, App: the
one owner of the spelling; the read side is the case-insensitive parse in
`App.OnStartup`), and the autostarted instance opens the window minimized,
not hidden: the frames stream either way, and a one-shot latch keeps the
minimize-to-tray intercept (ADR-0018 M2) from swallowing the login-time
minimize. A second launch while the autostarted instance runs needs no new
code: the single-instance guard (ADR-0018) signals it, the window activates
and comes forward, the second process exits. The entry is written at toggle
time and is not self-healing across an exe path swap (the vendor entry has
the same edge); a re-toggle rewrites the path. The seam is `IAutostartStore`
(read, write, null deletes) with the `RegistryAutostartStore` production
adapter; tests round-trip the real adapter through a temp HKCU subkey (the
`TwitchTokenStore` real-DPAPI precedent) or inject an in-memory fake (the
`SingleInstanceGuard` handle-factory seam precedent).

### Clock seconds and weather location hide (widget display rules)

The clock widget gains `ShowSeconds` (default off): digital mode only, the
formats grow the seconds (`hh:mm` / `HH:mm` become `hh:mm:ss` / `HH:mm:ss`),
AM/PM and the date badge are untouched, analog is untouched (it already has
a second hand), and the existing width-divisor font fit covers the
8-character string (the per-second memo already keys on the format). The
weather widget gains `Hide Location` (default off): when on, the header title
renders nothing (the resolved city and the UNKNOWN LOCATION placeholder are
both location names and are covered), while the CustomLabel (a label, not a
name) and the guidance lines (guidance, not names) still render; the value
rides `WeatherRenderModelKey` like the other visibility toggles. The
custom-label confirmation subtitle is deleted outright: a CustomLabel
renders nothing underneath it (the "still shows underneath" case is
unrepresentable).

### Global hotkeys (chord policy in Widgets, OS boundary in App)

The hotkey widget (`hotkey_button`) gains a `Global Hotkey` property (empty
means none), edited with the inspector's key-capture editor instead of typed:
`KeyCaptureModel` (App/Inspector) is the pure decision model the thin WPF
mapper drives (the `LocationSearchModel` precedent), reached through
`EditorKind.KeyCapture`. The stored value is the same plus-separated shape
the executor's `ParseVirtualKey` already validates, so the chord vocabulary
has one owner: `GlobalHotkeyChordPolicy.TryParseChord` (Widgets) parses the
chord into the `RegisterHotKey` operands (at least one modifier and exactly
one main key; a repeated modifier, a modifier-only chord, and an unknown key
are refused; a modifier-less chord would shadow the key system-wide), and
the Win32 MOD vocabulary (`MOD_ALT`/`MOD_CONTROL`/`MOD_SHIFT`/`MOD_WIN`, plus
`MOD_NOREPEAT`, which always rides the registration) lives there.

The App discovers hotkey-capable widgets through one optional interface,
`IGlobalHotkeyProvider` (Widgets; the `IWidgetEditorProvider` precedent, no
concrete-type `typeof` checks): `TryGetGlobalHotkey` (the stored chord parsed
into its operands) and `FireGlobalHotkey` (the widget's existing single fire
path, so the re-entrancy gate, the 30-second timeout, and the failure
logging are shared by the touch-up and the hotkey trigger). The OS boundary
is the `HotkeyApi` delegate bag (App/Hotkey; the `WinUsbApi` house pattern):
`RegisterHotKey`/`UnregisterHotKey` P/Invoke on the main window handle.
`GlobalHotkeyManager` (App/Hotkey) owns the registration state: the ids are
stable across refreshes, the refresh is a fully idempotent diff against the
OS (it runs on profile load, a widget placed/removed, a chord edit, a kill
switch toggle, and an interpreter path change), a duplicate chord is won by
the first widget in profile order (the later ones stay tap-only and log one
line), and a chord owned by another program is untracked with one log line
per cell per session. A hidden or minimized window keeps pumping its message
loop, so hotkeys work while the app is tray-hidden.

The hotkey actions "Next Page" and "Previous Page" (`HotkeyActionCatalog`)
route through the new context seam `IModernWigiDashContext.NavigatePage(int)`
(default no-op, the `PersistProperty` precedent; the App implements it over
`SwitchToPage`) instead of the SendInput executor, so the page boundary
clamps identically to a swipe (`ProfileOps.SetActivePageIndex` is the one
gate; an out-of-range step is a no-op, never a wrap: no page wrapping is a
deliberate non-goal). Tapping such a button on the display flips the page
the same way.

### Kill switch (machine-local; checked kills, default is live)

The settings hub's BEHAVIOR group kill switch is a checkbox, default
unchecked (live, the vendor parity). Checked (`AppSettings.KillSwitch`)
kills the integration, scoped to the OS-visible surface: no global-hotkey
registration (a tripped toggle unregisters the profile's chords) and the AHK
spawn is refused with a log line even from a tap. Every other action
(Launch, URL, media, page flip) keeps running from a tap. Rationale: some
games flag the background listener and the spawned script process as cheat
software, and the user wants one off switch for all of the integration.

### app_settings.json (machine-local settings, deliberately outside the profile)

The kill switch and the AHK interpreter path live in `app_settings.json`
beside `profile.json` in `%LOCALAPPDATA%\ModernWigiDash`
(`AppSettingsStore`, the `ProfilePersistence` shape): deliberately outside
the profile, so importing someone's profile can never overwrite the
machine-local choice; a corrupt or absent file repairs to the defaults with
one log line (the absent-service house pattern), and the save is atomic
(tmp + replace). The window holds the live value and the settings hub writes
through on change (the kill switch commits on selection, the interpreter
path on LostFocus, the Browse dialog commits through the same seam), so a
write-through is seen at the next spawn without a restart.

### Run AHK Script (user-supplied interpreter, nothing bundled)

The hotkey action "Run AHK Script" (`HotkeyActionCatalog`, `NeedsCommand`:
the command is the `.ahk` path, edited with the existing Path editor) routes
through the new context seam `LaunchAutoHotkeyScript(scriptPath)` (default
no-op, App-implemented) instead of the SendInput executor. The window
resolves the interpreter live from `app_settings.json` at spawn time: a
checked kill switch, a blank path, and a missing exe each refuse with one
log line (the absent-service house pattern: a refused action is an
observable no-op, never a throw). The spawn is bare through `AhkLaunchApi`
(App/Hotkey; the `HotkeyApi` delegate-bag precedent): the user's own
`autohotkey.exe` (a path chosen in the settings hub's Browse; nothing is
bundled or auto-detected) with the script as the argument, every trigger
launching a fresh interpreter, no running instance tracked. The vendor's
embedded AHK v1 engine (inside the x86 service) is deliberately not ported:
the app is 64-bit and service-less (ADR-0005), so the interpreter is the
user's own binary, spawned per trigger. The AHK scripts run outside our
process and cannot drive the display (no IPC surface is added, ADR-0005's
spirit): display control from a script is unrepresentable, and the display
control the hotkey actions have is the built-in page-nav actions only.

## Exit plan

1. The AHK interpreter's user-supplied shape is re-decided if bundling or
   auto-detection becomes legitimate (a first-party AHK v2 distribution with
   a license and an update story the app can own); until then, no bundling
   and no auto-detection are deliberate non-goals.
2. The autostart entry's path-snapshot edge (not self-healing across an exe
   swap) retires if it becomes user-visible: the Run entry's value would be
   re-compared against the running exe at process start.
3. The kill switch's scope (registration + AHK spawn only) is re-decided if
   the user-visible complaint widens (for example, the SendInput media keys
   also get flagged); the scope is one input to the registration resolution
   and one veto at the spawn, so widening it is a change at one site per
   scope member.
4. If the hotkey surface grows past the window handle (per-widget or
   per-profile registration sets), `GlobalHotkeyManager`'s one-HWND state is
   the seam to replace; the `HotkeyApi` delegate bag stays.

## Consequences

**Positive:**

- The Manager's two settings options are at parity without a service or an
  embedded engine: the autostart is a per-user registry write, and the AHK
  integration is a spawn of the user's own interpreter.
- The integration is killable with one checkbox, and the kill is
  machine-local: it survives profile travel (an import cannot re-enable what
  the user killed on this machine), and the refusal is a log line, so a
  killed integration degrades observably instead of silently.
- The OS boundary is seam-pinned: `HotkeyApi`, `AhkLaunchApi`, and
  `IAutostartStore` are injectable, so the registration diff, the kill-switch
  veto, the spawn policy, and the autostart round-trip pin against fakes
  (no real `RegisterHotKey` contention, no real interpreter, and the real
  registry adapter pins through a temp HKCU subkey), while the window-level
  pins (`WindowAutostartTests`, `WindowNavigatePageTests`,
  `WindowAhkScriptTests`) drive the live STA window through the same seams.
- Page navigation from a hotkey action and from a tap share one gate
  (`SetActivePageIndex`), so a button cannot wrap a page a swipe would
  clamp, and the seam's default no-op keeps test hosts and embedders
  unbound.

**Negative (the debt this ADR registers):**

- Global-hotkey registration is first-come: a chord another program holds is
  inert for us (tapping still works) with a log line, and the OS gives no
  way to share the chord or observe its release, so the idempotent diff
  re-checks on the documented triggers, not continuously.
- The Run entry's path is a snapshot at toggle time (the vendor-identical
  edge): an exe move silently breaks the autostart until a re-toggle.
- Every AHK trigger spawns a fresh interpreter process (the vendor's
  embedded engine kept one resident): a tap-spammed AHK button spawns a
  process per tap, and the app tracks none of them.
- `app_settings.json` is a second persisted file beside `profile.json`: the
  harness backup/restore covers both locations, and a profile with AHK
  buttons on a machine without the settings file degrades to refusal lines,
  not failures.

## Date

2026-08-26