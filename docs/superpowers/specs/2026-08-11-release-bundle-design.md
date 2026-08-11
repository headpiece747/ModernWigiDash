# Release Bundle — Design

**Date:** 2026-08-11
**Status:** Approved by design review (sections 1–4); Rev 1 — upstream release format discovery

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

## Rev 1 — Upstream release format discovery (2026-08-11)

During plan preparation the exact upstream releases were inspected and both
shipped **installers, not the zips assumed in the original decisions**. The
affected decisions were revised in place (below) to match reality:

| Component | Latest release | Assets | Installs to | Service registered |
|---|---|---|---|---|
| LibreHardwareService | v0.3.4 (2026-07-05) | `LibreHardwareService.msi` only | `C:\Program Files\LibreHardwareService\` | `LibreHardwareService` (LocalSystem, Auto) |
| PresentMon | v2.5.1 (2026-06-29) | `PresentMon-v2.5.1.msi` + bootstrapper exe + symbols | `C:\Program Files\Intel\PresentMonSharedService\` | `PresentMonSharedService` ("PresentMon Shared Service", Auto) |

Both were verified against a live install: the LHS MSI and the PresentMon MSI
each register their service (LocalSystem, auto-start) during install. The
client-side PresentMon API (`pmOpenSession`) discovers the service regardless of
its name, so the app works unchanged with `PresentMonSharedService`. Service
names referenced in the README were corrected (`PresentMonService` →
`PresentMonSharedService`).

Consequences:
- `setup-telemetry.bat` runs the two MSIs silently; it no longer does
  `sc create` / `sc config`. Idempotency = msiexec repair/upgrade semantics.
- The zip grows to roughly **230 MB** (LHS MSI 60 MB + PresentMon MSI 158 MB).
  The user's request was to include both components in the zip, so this is
  accepted.
- `binPath` quoting / move-repair concerns disappear: the MSIs install to
  Program Files, independent of the zip's location.

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
   ├─ LibreHardwareService/      ← LibreHardwareService.msi + its MPL-2.0 LICENSE
   ├─ PresentMon/                ← PresentMon-v2.5.1.msi + its MIT LICENSE
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
2. **Runs both MSIs silently** (paths derive from `%~dp0`, quoted):
   - `msiexec /i "%~dp0telemetry\LibreHardwareService\LibreHardwareService.msi" /qn /norestart`
   - `msiexec /i "%~dp0telemetry\PresentMon\PresentMon-v2.5.1.msi" /qn /norestart`
3. **Idempotent**: re-running the same MSI versions performs a repair/upgrade,
   so "run it once after installing or updating" is all the user ever needs.
4. **Per-component status output**: `[OK] <name> installed` or `[FAIL] <name>`
   with the `msiexec` exit code (0 = success).
5. PresentMon fallback: if the MSI exits non-zero, retry with the Burn
   bootstrapper `PresentMon-2.5.1-x64.exe /quiet /norestart` before reporting
   failure (covers prerequisite handling the MSI alone may require).

Uninstall is README-only: remove both programs via Settings → Apps (or
`msiexec /x`), then delete the zip folder. No separate script.

## Decision 3 — `scripts/build-release.ps1`

Run from the repo root with no arguments. Uses a temp staging dir; never writes
downloaded third-party binaries into the repo.

1. **Publish the app** with the verified single-file command:
   `-c Release -r win-x64 --self-contained` + `PublishSingleFile`,
   `PublishReadyToRun`, `IncludeNativeLibrariesForSelfExtract`,
   `EnableCompressionInSingleFile`, `DebugType=None`, `DebugSymbols=false`.
   Native (SkiaSharp) PDBs are stripped by the shared build target.
2. **Download pinned LHS + PresentMon** release **MSIs** from GitHub into a
   build-time cache. Versions are script constants, overridable via
   `-LhsVersion` / `-PresentMonVersion`:
   - `LibreHardwareService.msi` (v0.3.4) → `telemetry\LibreHardwareService\`
   - `PresentMon-v2.5.1.msi` (v2.5.1) **and** the bootstrapper fallback
     `PresentMon-2.5.1-x64.exe` → `telemetry\PresentMon\`
3. **Download upstream license files** verbatim into
   `telemetry\third-party-licenses\` (LHS MPL-2.0 `LICENSE`, PresentMon MIT
   `LICENSE.txt` — raw.githubusercontent.com at the pinned tags) and generate
   `NOTICES.txt` listing each component and its public source repo.
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
    - Re-run the batch once (repair/upgrade; safe to repeat).
    - Check the services in services.msc: LibreHardwareService and
      "PresentMon Shared Service" — both should be Running / Automatic.

== Updating ==

  Download the new zip, unzip over the old folder (or delete it), and
  re-run setup-telemetry.bat once. Profiles and settings are kept.

== Uninstalling ==

  1. (If you installed telemetry) remove both programs:
     Settings -> Apps -> Installed apps ->
       "LibreHardwareService"     -> Uninstall
       "Intel PresentMon"         -> Uninstall
  2. Delete the ModernWigiDash-win-x64 folder.

== License & credits ==

  ModernWigiDash            MIT  (LICENSE-ModernWigiDash.txt)
  LibreHardwareService      MPL-2.0  — https://github.com/epinter/LibreHardwareService
  PresentMon Service        MIT  — https://github.com/GameTechDev/PresentMon
  See  telemetry\third-party-licenses\NOTICES.txt  for full texts + sources.
```

## Error handling & edge cases

- **Re-run after update**: `setup-telemetry.bat` re-runs are idempotent
  (msiexec repair/upgrade semantics), and a moved zip folder needs no repair —
  the MSIs install to Program Files.
- **MSI prerequisites**: if the PresentMon MSI exits non-zero, the batch retries
  with the bundled Burn bootstrapper before reporting failure.
- **Missing display / driver**: README troubleshooting (plug in first, Zadig /
  vendor driver). The app itself degrades to "not connected" with no crash.
- **Download failure during build**: `build-release.ps1` exits non-zero with the
  failing URL; no partial zip. `-SkipTelemetry` is the escape hatch.
- **Upstream drift**: versions are pinned constants; the manifest printout at the
  end of the build makes any unexpected change visible before shipping.

## Verification

1. Run `scripts/build-release.ps1` end-to-end on the development machine.
2. Confirm the zip manifest: exe + `Resources/`, `README.txt`,
   `setup-telemetry.bat`, `LICENSE-ModernWigiDash.txt`, `telemetry/` with both
   MSIs and the license files.
3. Extract to a **path with spaces** on the real machine; run
   `setup-telemetry.bat` (admin); confirm both services show Running / Automatic
   in `services.msc`; re-run the batch and confirm it reports success (repair).
4. Launch the app with the display attached; confirm Hardware Monitor and
   FPS / Frame Time widgets show live data.
5. Uninstall path: uninstall both programs via Settings → Apps, delete the
   folder.
6. `-SkipTelemetry` produces a valid app-only zip.

## Out of scope / future

- GitHub Actions release workflow (tag push → build → attach zip). Design is
  script-first; CI wiring is a later step.
- Bundling a signed driver INF. Driver install remains vendor/Zadig guidance.
- Auto-starting telemetry from the app process (rejected: couples the app to the
  bundled services and complicates service-context behavior).
