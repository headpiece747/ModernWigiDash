# ADR-0021: the theme file lives in the user state dir and rides the export bundle as an optional item

**Date:** 2026-08-27
**Status:** Accepted
**Deciders:** Project owner

## Context

The theme persisted to `app_theme.json` next to the executable
(`AppContext.BaseDirectory`), a location that was never actually decided. It
carried three hazards:

- A stale dev-machine copy in `bin\Release` silently overrode every color on
  every launch until deleted (a real 2026-08-26 case: a stale
  `AccentGreen=#12141D` hid the Connected badge's green dot behind the amber
  label while the badge still read "Connected").
- The Debug-vs-Release split: two build outputs, two theme files; whichever
  build the user last ran owned the colors.
- An install-dir swap (the updater's rename-aside) carries or loses the theme
  with the files, while every other piece of app state already survived it.

The rest of the app's state already lives in
`%LOCALAPPDATA%\ModernWigiDash` (profile.json, app_settings.json, the logs),
for exactly the reasons pinned in the App ctor: a Program Files install is
read-only for standard users, and a single-file host's BaseDirectory is the
extraction dir under %TEMP%. The release notes
(`release\README.txt`) already documented the theme under that folder; the
code had drifted from the documented location.

Cross-machine travel was the second open question: the profile export is the
app's portability contract, and the verified pattern across JetBrains tools
and VS Code (plus Sublime, GIMP, and Unity) is that the theme rides in the
export bundle, restore is a deliberate per-item choice, and the profile
import itself never touches the theme file.

## Decision

1. **The theme file lives in the user state dir**:
   `%LOCALAPPDATA%\ModernWigiDash\app_theme.json`, beside profile.json and
   app_settings.json (`ThemeSettings.DefaultPath`, the one read/write
   location; the save creates the directory when missing). The exe-dir copy
   (`ThemeSettings.LegacyPath`) is a read-only one-time migration source:
   when the state file is absent and a parseable legacy copy exists, `Load`
   carries it across and logs one line, so an upgraded install keeps the
   colors the user last saw. A corrupt or absent legacy copy is a no-op
   (a corrupt one logs one line). Once the state file exists, the exe dir is
   never consulted again, and a corrupt state file degrades to the defaults
   the pre-ADR-0021 way (the migration is not a corrupt-file repair).
2. **The theme rides the profile export bundle as an optional section**
   (`ProfileExportTheme`, the top-level `"theme"` key, a sibling of the
   profile's own fields). The manual export composes it
   (`WithTheme`); the one size-guarded import boundary reads it back
   (`ReadTheme`) and the `ProfileImportOutcome.Loaded` verdict carries it as
   `BundledTheme`, so no caller ever re-reads the file unguarded. The
   persisted profile.json never carries the section: `ProfileOps.ExportJson`
   stays a bare profile (pinned), the profile parse ignores the unknown key,
   and the boot load ignores the verdict's theme field, so a hand-edited
   profile.json can never restore a theme. A bundled theme is untrusted
   input: bounded by the import file-size guard and validated per-property
   at apply time (the `ParseColor` rule through the ThemeManager's
   skip-invalid-hex), so a hostile or hand-broken section degrades
   property-by-property exactly like a corrupt local theme file.
3. **The restore is a deliberate per-item choice**: the manual import offers
   the bundled theme only when its fingerprint
   (`ThemeApplicator.Fingerprint`, the one theme change signal) differs from
   the current theme, and applies it only behind the user's confirm
   (`DialogHost.Confirm`). A default-themed export never prompts a
   default-themed machine. A failed state-dir write surfaces one line and
   the colors still apply for the session (the ThemeDialog's rule). The
   profile import itself never touches the theme file.

## Consequences

- The stale-copy hazard is retired by construction: the exe dir is no longer
  a read/write theme location, and the Debug-vs-Release split is gone (one
  file for all of a user's builds).
- Upgraded installs migrate the legacy copy exactly once; the display log's
  `[THEME] Migrated legacy theme file from ... to ...` line dates it. The
  harness (`wmd-verify.ps1`) backs up and restores both locations (state dir
  primary, exe-dir copy as `app_theme.exe-dir.json`) so a verify run can
  neither trigger an unexpected migration nor lose a deliberately placed
  legacy copy.
- An import of a bundle carrying a different theme is one confirm dialog,
  offered only after the profile swap succeeded, so a declined or failed
  theme never undoes the imported profile.
- The updater needs no change: it preserves unknown install-dir files
  generically, and the theme file simply no longer lives there
  (`UpdateScriptTests`' sample user file is a neutral name now).
- The lockstep pin (`ThemeSettingsTests`): the theme path must start with
  the profile's state dir, so the two owners of the dir name can never
  drift apart.
- Exit: the legacy-migration leg retires when no pre-ADR-0021 install can
  exist on a user's machine anymore (the `File.Exists` on the exe dir stops
  mattering); the bundle section retires with the export feature. Until
  then the leg is a cheap no-op check per launch.

## Date

2026-08-27
