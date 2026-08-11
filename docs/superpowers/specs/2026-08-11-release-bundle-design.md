# Release Bundle — Design

**Date:** 2026-08-11
**Status:** Approved by design review (sections 1–4)

## Goal

Ship a user-facing release zip, `ModernWigiDash-win-x64.zip`, that bundles the
single-file app together with LibreHardwareService and PresentMon Service so a
non-technical user can go from download to working display in three steps —
with hardware/FPS telemetry available but strictly optional.

## Licensing research (bundling is allowed)

| Component | License | Redistribution |
|---|---|---|
| LibreHardwareService (epinter) | MPL-2.0 | Allowed. Include its LICENSE verbatim and point at the public source repo. No code is linked — it runs as a separate process. |
| PresentMon Service (GameTechDev/Intel) | MIT | Allowed. Include its LICENSE verbatim. The app already loads `PresentMonAPI2.dll` from the installed service dir at runtime; we never ship the API DLL. |

Neither component is linked into the app binary; both remain separate processes
communicating over shared memory (LHS) and the PresentMon API (named pipe). The
MPL-2.0 file-level copyleft does not extend across process boundaries here, and
the source for both is publicly available, so redistribution is compliant.

## Decision 1 — Package layout

`build-release.ps1` produces `ModernWigiDash-win-x64.zip` that unzips to a single
folder named `ModernWigiDash-win-x64`:

```
ModernWigiDash-win-x64/
├─ ModernWigiDash.App.exe        ← single-file self-contained (just run it)
├─ Resources/                    ← fonts, theme, icons (publish output)
├─ README.txt                    ← the full user guide
├─ setup-telemetry.bat           ← optional, one-time, run as Admin
├─ LICENSE-ModernWigiDash.txt    ← MIT (app)
└─ telemetry/
   ├─ LibreHardwareService/      ← LHS release binaries + its MPL-2.0 LICENSE
   ├─ PresentMon/                ← PresentMon Service release binaries + its MIT LICENSE
   └─ third-party-licenses/      ← generated NOTICES.txt + upstream license files
```

Rationale: the display-only experience works with zero setup; telemetry is
clearly separated and labeled optional; licenses are grouped and copied verbatim
from upstream. No INF/driver is bundled — the driver story lives in the README
(vendor driver or Zadig), keeping the zip small and avoiding driver-signing
liability.

## Decision 2 — `setup-telemetry.bat`

Checked in at `release/setup-telemetry.bat`; copied into the zip as-is.

Behavior:
1. **Auto-elevates** if not already admin (UAC prompt via PowerShell), so
   "double-click → yes" is the whole flow.
2. **Registers + starts both services** with `start= auto` (survive reboots):
   - `LibreHardwareService` → `telemetry\LibreHardwareService\LibreHardwareService.exe`
   - `PresentMonService` → `telemetry\PresentMon\PresentMonService.exe`
   - All paths derive from `%~dp0` so the folder may be unzipped anywhere,
     including paths with spaces; `binPath` is quoted correctly.
3. **Idempotent**: existing services are stopped, `sc config` updates the
   `binPath` to the current folder, and they are restarted — re-running after a
   move/re-download repairs paths instead of erroring.
4. **Per-service status output**: `[OK] installed & running` or `[FAIL]` with the
   `sc` error.

Uninstall is README-only (`sc stop` + `sc delete` for both names), not a separate
script.

## Decision 3 — `scripts/build-release.ps1`

Run from the repo root with no arguments. Uses a temp staging dir; never writes
downloaded third-party binaries into the repo.

1. **Publish the app** with the verified single-file command:
   `-c Release -r win-x64 --self-contained` + `PublishSingleFile`,
   `PublishReadyToRun`, `IncludeNativeLibrariesForSelfExtract`,
   `EnableCompressionInSingleFile`, `DebugType=None`, `DebugSymbols=false`.
   Native (SkiaSharp) PDBs are stripped by the shared build target.
2. **Download pinned LHS + PresentMon** release zips from GitHub into a build-time
   cache. Versions are script constants, overridable via
   `-LhsVersion` / `-PresentMonVersion`:
   - LHS zip → `telemetry\LibreHardwareService\`
   - PresentMon zip → extract its `Service/` subfolder only → `telemetry\PresentMon\`
3. **Download upstream license files** verbatim into
   `telemetry\third-party-licenses\` (LHS MPL-2.0 `LICENSE`, PresentMon MIT
   `LICENSE.txt`) and generate `NOTICES.txt` listing each component and its
   public source repo.
4. **Copy the checked-in templates** from `release/`: `README.txt`,
   `setup-telemetry.bat`, plus `LICENSE-ModernWigiDash.txt` (repo root `LICENSE`).
5. **Zip** the staged folder (with the `ModernWigiDash-win-x64\` root folder
   inside) → `ModernWigiDash-win-x64.zip`, then print a contents manifest + size.
6. **`-SkipTelemetry`** switch builds an app-only zip (same layout minus
   `telemetry/`). A failed download fails the build loudly — never a partial zip.

## Decision 4 — `README.txt`

Checked in at `release/README.txt`; copied into the zip as-is. Plain text,
Notepad-friendly, no markdown. Full text:

```text
ModernWigiDash — G.Skill WigiDash widget stack
===============================================

== Quick Start (3 steps) ==

  step 1.  Plug the display into a USB port   BEFORE starting the app.
  step 2.  Double-click  ModernWigiDash.App.exe
  step 3.  OPTIONAL telemetry (hardware / FPS widgets):
           right-click  setup-telemetry.bat  →  Run as administrator.
           (one time only — installs two background services)

  Done. The display shows your widgets. Nothing else needs installing.


== What's in this folder ==

  ModernWigiDash.App.exe       the app — just run this
  Resources/                   fonts, theme, icons (needed — keep it next to the exe)
  setup-telemetry.bat          optional: installs the telemetry services (Admin)
  README.txt                   this file
  LICENSE-ModernWigiDash.txt   the app's MIT license
  telemetry/                   bundled LibreHardwareService + PresentMon (see below)

== Do I need the telemetry? ==

  No. Everything works with just the app:
    - Clock, Stopwatch, Audio Visualizer, Now Playing, Twitch, Hotkey,
      Stock & Crypto, Picture & GIF, Weather, Text  — all fine.
    - Two widgets need a background service, and only those two:
        Hardware Monitor  <- needs LibreHardwareService
        FPS / Frame Time  <- needs PresentMon Service
  Without the services those widgets show a graceful "unavailable" state.
  Both services are bundled and installed by  setup-telemetry.bat  (Admin).
  They run in the background and start automatically with Windows.

== First run ==

  - Add widgets: click a page in the editor, pick widgets from the catalog,
    drag / resize / rotate, and press the checkmark to save.
  - Swipe left/right on the display to switch pages.
  - Toggle the layout editor on/off from the app window.

== Data locations & reset ==

  Profile + settings:  %LOCALAPPDATA%\ModernWigiDash\
  Theme:               app_theme.json  (same folder)
  Twitch tokens:       DPAPI-encrypted, same folder (keyed to your Windows user)
  Reset:               close the app, delete that folder, relaunch.

== Troubleshooting ==

  "Display not connected" in the app bar?
    - The display must be plugged in BEFORE the app starts.
    - It needs the WinUSB driver bound. The vendor's WigiDash software
      installs it; otherwise bind it with Zadig:
        Device:  USB\VID_28DA&PID_EF01  ->  Driver: WinUSB  ->  Replace.
    - Unplug / replug after installing the driver, then restart the app.

  Windows SmartScreen shows "Windows protected your PC"?
    The release exe is unsigned. Click  More info -> Run anyway.

  Telemetry widgets still "unavailable" after setup-telemetry.bat?
    - Re-run the batch once (it repairs service paths if you moved the folder).
    - Check the services in services.msc: LibreHardwareService,
      PresentMonService — both should be Running / Automatic.

== Updating ==

  Download the new zip, unzip over the old folder (or delete it), and
  re-run setup-telemetry.bat once. Profiles and settings are kept.

== Uninstalling ==

  1. Delete the ModernWigiDash-win-x64 folder.
  2. (If you installed telemetry) remove the services, as Admin:
        sc stop  LibreHardwareService & sc delete  LibreHardwareService
        sc stop  PresentMonService     & sc delete  PresentMonService

== License & credits ==

  ModernWigiDash            MIT  (LICENSE-ModernWigiDash.txt)
  LibreHardwareService      MPL-2.0  — https://github.com/epinter/LibreHardwareService
  PresentMon Service        MIT  — https://github.com/GameTechDev/PresentMon
  See  telemetry\third-party-licenses\NOTICES.txt  for full texts + sources.
```

## Error handling & edge cases

- **Move/re-download after install**: `setup-telemetry.bat` re-runs are idempotent
  and repair `binPath` via `sc config`.
- **Paths with spaces**: all batch paths derive from `%~dp0`; `binPath` quoted.
- **Missing display / driver**: README troubleshooting (plug in first, Zadig /
  vendor driver). The app itself degrades to "not connected" with no crash.
- **Download failure during build**: `build-release.ps1` exits non-zero with the
  failing URL; no partial zip. `-SkipTelemetry` is the escape hatch.
- **Upstream layout drift**: PresentMon's `Service/` subfolder and LHS zip layout
  are pinned versions; the manifest printout at the end of the build makes any
  unexpected file set visible before shipping.

## Verification

1. Run `scripts/build-release.ps1` end-to-end on the development machine.
2. Confirm the zip manifest: exe + `Resources/`, `README.txt`,
   `setup-telemetry.bat`, `LICENSE-ModernWigiDash.txt`, `telemetry/` with both
   services and the license files.
3. Extract to a **path with spaces** on the real machine; run
   `setup-telemetry.bat` (admin); confirm both services show Running / Automatic
   in `services.msc`; re-run the batch and confirm idempotent repair.
4. Launch the app with the display attached; confirm Hardware Monitor and
   FPS / Frame Time widgets show live data.
5. Uninstall path: `sc stop`/`sc delete` both services, delete the folder.
6. `-SkipTelemetry` produces a valid app-only zip.

## Out of scope / future

- GitHub Actions release workflow (tag push → build → attach zip). Design is
  script-first; CI wiring is a later step.
- Bundling a signed driver INF. Driver install remains vendor/Zadig guidance.
- Auto-starting telemetry from the app process (rejected: couples the app to the
  bundled services and complicates service-context behavior).
