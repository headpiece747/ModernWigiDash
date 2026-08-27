# Theme dialog

The chrome theme: a modal dialog that edits the app's non-canvas colors (window chrome, panels, text), with Apply persisting them to `app_theme.json` **in the user state dir** (`%LOCALAPPDATA%\ModernWigiDash`, beside `profile.json` and `app_settings.json`; ADR-0021). A fresh install has no theme file and gets the built-in defaults (the contract a GitHub download ships). The legacy exe-dir copy (pre-ADR-0021) is a read-only one-time migration source, and a profile export can travel the theme to another machine (the bundle's optional `"theme"` section; the manual import offers it behind one `Restore Theme` confirm when its fingerprint differs, declining leaves the state-dir file untouched). It opens nested over the settings hub (the hub's Appearance group is the opener; see `settings-hub.md`).

## Sub-features

- `theme-open` opens the dialog from the settings hub's Appearance group.
- `theme-cancel` closes without persisting.
- `theme-apply` persists an edited color to `app_theme.json`.
- `theme-reset` restores default colors inside the open dialog (no persistence until Apply).

## How to get to it (user POV)

- Choose the `⚙️ Settings` button in the top bar, then `Customize theme colors...` in the Appearance group.
- (Keyboard.) Press `Escape` in the dialog to cancel it; `Escape` in the hub closes the hub.

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor` and `backup-profile` ran.
- No other theme dialog is open (the dialog is modal while open).

- **Open dialog.** Open the hub (`click BtnSettings`), run `click "Customize theme colors..."`. Then `wait "Theme Customization" -Seconds 10` returns a window named `🎨 Theme Customization`. Proof: `wait` output + `doctor -Window "Theme Customization"`.
- **Cancel.** Choose `Cancel` in the dialog. Run `click Cancel`. The dialog closes. Proof: `wait "Theme Customization"` times out (expected negative) and `backup-profile`'s `app_theme.json` (if present) is byte-identical to the on-disk file after the close.
- **Apply a color.** In the open dialog, change one hex value (each themeable row has a hex Edit above its preview). Run `find <color-label>` to locate the row, confirm a writable Edit child, `set <edit-handle> #123456`, then `click Apply`. The dialog closes and the chrome re-themes. Proof (mutation): `app_theme.json` on disk now contains `#123456`; `shot` of the main window shows the changed chrome.
- **Reset.** Reopen the dialog (`click BtnSettings`, `click "Customize theme colors..."`), run `click Reset`, `shot` the dialog. The hex Edits show default values again; on disk nothing changed until Apply. Proof: `find <same-color-label>` hex values match the defaults and `app_theme.json` is unchanged.
- **Evidence.** Run `shot <evidence>/theme/after-apply.png` after the Apply step and `find <color-label>` before and after; save both `find` outputs as `<evidence>/theme/before.txt` / `after.txt`.

## Gotchas

- The dialog is modal over the hub, which is modal over the main window: two modals deep while open. Track both with `wait`/`doctor -Window`, close the theme dialog first, then the hub, before driving the main window.
- `Apply` is disabled until every hex entry validates; a failed `set` leaves it disabled. Check the Apply state (or just look at the `shot`) before blaming the click.
- Resetting then applying persists the *defaults*, not the previous values. Backups (not memory) are the restore path.
- A `Theme Save Failed` message box means `app_theme.json` could not be written (read-only dir). It is a product signal, not a harness gap: report it, don't retry.
- The theme file lives in the LocalAppData state dir (the real location since ADR-0021; the former next-to-the-exe location carried a stale-copy hazard: a stale dev-machine copy in `bin\Release` silently overrode every color on every launch. Real case (2026-08-26): a stale `AccentGreen=#12141D` (near-background navy) made the Connected badge's "green" dot invisible while the label still read `Connected` in amber). Before concluding "the theme is the defaults", check `app_theme.json` in `%LOCALAPPDATA%\ModernWigiDash` (the real location) AND any exe-dir legacy copy (the app migrates it once on the next launch when the state file is absent). `backup-profile` backs up both (the exe copy as `app_theme.exe-dir.json`) and `restore-profile`/`clean` route each back to its location.
- Theme changes apply to the running session immediately; a relaunched app re-reads `app_theme.json` at startup. The restore via `clean` covers reruns, not the relaunch.