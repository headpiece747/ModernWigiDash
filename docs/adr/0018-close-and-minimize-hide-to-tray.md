# ADR-0018: Close and minimize hide to the tray when opted in; only the tray's Quit exits

**Date:** 2026-08-25
**Status:** Accepted
**Deciders:** Project owner

## Context

The window was the only handle on the app: closing it ran the full teardown,
the display went to the vendor standby ritual (welcome screen + sleep command,
backlight off), and the next use meant relaunch and a full USB init. For a
display app that is expected to keep running (live weather, prices, Twitch,
hardware telemetry), one accidental X cost the whole session. But the hide
must never strand the app: a window hidden without a reliable way back leaves
a display that is still streaming with no control surface, and a second
instance launched "to get it back" would fight the first for the USB device.

## Decision

The close behavior is a persisted profile setting, and the default stays the
pre-feature behavior (exit). The vocabulary and parse rule live in one place,
`CloseBehaviorPolicy` (Core/Models): `quit` (X, Alt+F4, and minimize run the
full teardown) and `hideToTray` (the window hides, the display keeps
streaming, and the tray's "Quit" is the only exit). The value is a raw string
in the profile JSON (the ThemeSettings precedent: the profile is a
hand-editable traveling artifact); null (absent) or unknown (hand-edited,
case-sensitive) values degrade to `quit` at every runtime read. The setting
travels with export and import: an imported profile lacking the field keeps
the local value (`CloseBehaviorPolicy.MergeImport`, called from the window's
import handler), and a present-but-corrupt field is sanitized to `quit` by
`ProfileImportSanitizer`.

The window's close (X, Alt+F4) and its minimize are intercepted by one policy,
`CloseInterceptPolicy` (App): hide to the tray only when the resolved
behavior is `hideToTray` AND the tray icon is live. With no live tray icon the
user would have no way back to the window, so the intercept falls through to
the normal behavior (the N1 fallback: an honest, observable degradation
instead of a silent mode switch). One predicate serves both intercepts, so
close and minimize cannot drift.

The tray icon is always present, independent of the setting and the window
state (Q4). It is a WinForms `NotifyIcon` behind the `ITrayIconSurface` seam
(the App adds the WindowsForms framework reference; `UseWindowsForms` stays
off), driven by `TrayIconController`, which owns the icon's lifetime as the
`Tray` startup wiring step and a teardown-plan step. A single click on the
icon shows and activates the window; the context menu is "ModernWigiDash",
separator, "Quit". At start the controller logs an honest verdict derived
from the surface's liveness ("icon shown", or "icon NOT shown (icon file
missing or unreadable): the N1 guard falls the close through to a normal
exit"), so the N1 fallback and the icon failure are both visible in the
display log. The ico loads from a physical `Resources/Logo/logo.ico` next to
the executable, which is why the csproj carries it as a `Resource` item
(embedded for the WindowChrome pack-URI resolution) AND a `None` item (the
output copy): MSBuild's copy targets walk `Content`/`None`/
`EmbeddedResource`/`Compile` only, so `CopyToOutputDirectory` on a `Resource`
item is dead metadata (the 2026-08-25 live verification caught the tray
silently absent until the `None` copy was added).

A single-instance guard (`SingleInstanceGuard`, App) makes the tray keep-alive
safe against double launches: a named mutex (per-session scope, each user
session may run its own instance and the tray icon is per-session anyway)
claims the instance, and a named manual-reset event carries the "show
yourself" signal. A second launch finds the mutex already claimed, signals
the event (the primary hops to its dispatcher and shows/activates the
window), and exits before its engine starts. The kernel releases both handles
on process death (clean exit, crash, or force-kill), so a dead instance can
never wedge the next launch. The guard is wired production-only
(`IsProductionEntry`): under a test host the entry assembly is the test
runner, and the secondary path's WPF `Shutdown` would shut down the test's
own `Application` mid-invoke, leaving a half-shut-down static state that
FailFasts the next test's resource load. The handle factories are injected
(the `MemoryMappedLhmMapSource` seam precedent), so the verdict and signal
policy are pinned against in-memory handles.

The setting is edited in the settings hub (`SettingsDialog` /
`SettingsModel`, App/Dialogs): a 460x560 grouped dialog (APPEARANCE /
BEHAVIOR / PROFILE) opened from the title bar's "Settings" button, the slot
that opened the theme dialog directly before this feature. The theme editor
now opens nested from the hub's APPEARANCE group, and the profile
export/import actions live in its PROFILE group. The close-behavior radios
write through on change: the value persists the moment a radio is checked
(`SettingsModel` hands the commit to the window's `CommitCloseBehavior`), so
there is no apply-on-close to lose.

While hidden, the display stays fully live (the B1 choice): the render tick,
the frame delivery, and the USB streaming continue untouched; the hide is
window-only, and the session-end standby path (`RunSessionEndStandby`, wired
to `SessionEnding`) is unchanged.

## Exit plan

1. The N1 fallback (close falls through to a normal exit when the tray is
   not live) retires when tray liveness is guaranteed by construction (the
   ico always bundled and the notification area verified at start, or a tray
   surface that reports liveness per operation) or when the tray surface
   changes. Until then the honest verdict + fall-through is the safe default.
2. The single-instance guard's per-session scope is re-decided if
   multi-instance becomes legitimate (for example, two displays from two
   profiles); the guard's handle seam already isolates that decision to the
   mutex/event names.
3. The WinForms `NotifyIcon` adapter is replaced if a native WPF tray path
   lands (or the app leaves WinForms); the seam, not the adapter, is the
   decision.
4. The setting's import merge and sanitization are re-decided if the profile
   import's trust boundary changes.

## Consequences

**Positive:**

- An accidental X no longer kills the session: the display keeps streaming,
  and the app is one tray click away. The default is unchanged, so existing
  users keep the pre-feature teardown; the new behavior is opt-in and travels
  with the profile.
- The hide is safe by construction: one predicate gates both intercepts,
  tray liveness is a required operand, and a broken tray degrades to the old
  behavior with a log line instead of stranding the window.
- Double launches cannot steal the display: the second instance signals the
  first and exits before its engine starts, and a dead instance cannot wedge
  the next launch (kernel-released handles).
- The decisions are testable without pixels: the vocabulary/parse/import
  merge (`CloseBehaviorPolicy`), the intercept predicate
  (`CloseInterceptPolicy`), the guard's verdict + signal policy (in-memory
  handles), the tray controller's routing + honest verdict
  (`TrayIconController`), and the hub model (`SettingsModel`) are all pinned
  at module interfaces; the window's intercept and the session-end standby
  run on a live STA window (`WindowCloseInterceptTests`).

**Negative (the debt this ADR registers):**

- The tray icon is WinForms on a WPF app: the App project references the
  WindowsForms framework, and the ico is resolved twice, once from the
  embedded resource blob (the WindowChrome pack URI) and once from a physical
  file next to the exe (the tray surface's disk load), held in lockstep by
  the Resource + None dual csproj items.
- A second launch's diagnostic lines can be silently lost at its exit:
  `FileLog` is best-effort (it swallows `IOException`), and the secondary's
  final flush runs at process exit. An observed transient exclusive lock on
  the log file (antivirus/indexer) dropped the secondary's three buffered
  lines during the 2026-08-25 live verification while the same file read
  fine a minute later; the activation and the exit itself did not depend on
  the log.
- A hand-edited or legacy profile without the setting resolves to `quit` on
  every read, and the resolved value is stamped on the next export, so a
  profile round-tripped through the app gains the explicit key.

## Date

2026-08-25