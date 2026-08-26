# Settings hub

The top-bar `⚙️ Settings` button opens a modal hub with three groups. Appearance opens the theme dialog (`theme.md`). Behavior's close-behavior radios persist to the profile the moment a radio is checked: no Apply step, the radio write is the change. Profile carries the export/import actions moved from the status bar (`profile-roundtrip.md`).

## Sub-features

- `hub-open` opens the hub from the top bar and proves its three groups.
- `hub-close-behavior` persists a close-behavior choice to the profile's `CloseBehavior` value.
- `hub-close-hides` with the keep-alive setting on, closing the window hides it to the tray (the app keeps running); a second launch brings the hidden window back.
- `hub-profile-actions` routes the Profile group's buttons to the round-trip flows.

## How to get to it (user POV)

- Choose the `⚙️ Settings` button in the top bar.
- (Keyboard.) Press `Escape` in the hub to close it.

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor`; `backup-profile` ran.
- No settings hub is open (it is modal while open).

- **Open.** Run `click BtnSettings`. Then `wait "Settings" -Seconds 10` returns the hub window. Proof: `dump -Path <evidence>/settings/hub.txt` shows a window titled `Settings` whose text includes the group headers `APPEARANCE`, `BEHAVIOR`, and `PROFILE`.
- **Persist the close behavior.** In the Behavior group, run `click "Keep running in the tray when the window closes"`. Proof (second view): the persisted `profile.json` (the app's profile file next to the executable, read directly) now carries `"CloseBehavior": "hideToTray"`. The write is immediate: no Apply, no close step between the check and the read.
- **Restore the default.** Run `click "Quit when the window closes"`. Proof: `profile.json` carries `"CloseBehavior": "quit"`.
- **Hide on close.** With the keep-alive setting persisted (the step above, reversed), close the main window: read the window's close box (the `Close` button in the `dump` of the main window) and `click-at <x> <y>` it. Proof: `wait "ModernWigiDash"` times out (the window is hidden, not closed) and the app process is still alive (the harness `doctor` process read still lists it). Minimize behaves the same: with the setting on, a minimize also hides to the tray.
- **Restore the hidden window.** Launch a second instance (`launch` again; the second launch signals the first and exits). Proof: `wait "ModernWigiDash" -Seconds 10` returns the main window - the single-instance guard's activation brought the hidden window forward. (The tray icon's own single-click show is user-manual: the icon lives in the OS notification area, outside the app's UIA tree.)
- **Close without changing anything.** Open the hub and press Escape (`wmd-verify.ps1` has no key press; use the hub's close box or re-verify the main window drives after `wait "Settings"` times out). Proof (negative): `profile.json` is byte-identical to its pre-open read.
- **Evidence.** Keep the hub `dump`, the `profile.json` reads (before and after each radio), and the round-trip evidence under `<evidence>/settings/`.

## Gotchas

- The hub is a modal app window: the main window is dead while it is open. Track it with `wait`/`doctor -Window "Settings"`, and never drive the main window until the hub is proven closed.
- The opener's button name (`⚙️ Settings`) and the hub's window title (`Settings`) share a needle: open by AutomationId (`click BtnSettings`), find the hub by window title.
- The theme dialog opens nested over the hub: two modals deep. Close the theme dialog first, then the hub.
- An unknown or missing `CloseBehavior` value (hand-edited profile, older export) seeds the default radio (`quit`); the hub never shows a mystery selection.
- A hidden window with the setting on has no taskbar entry: the only exits are the tray icon's `Quit` (user-manual) or a process-level stop. The single-instance guard means a second `launch` never starts a second app - it activates the hidden window instead.
- The tray icon lives in the OS notification area, not in the app's UIA tree: the harness can prove the hide (window gone, process alive) and the second-launch restore, but not the icon click itself.