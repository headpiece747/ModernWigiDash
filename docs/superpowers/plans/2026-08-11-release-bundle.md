# Release Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a user-facing `ModernWigiDash-win-x64.zip` that bundles the single-file app with LibreHardwareService + PresentMon Service as pinned MSIs, plus a README and an auto-elevating `setup-telemetry.bat`, so a non-technical user goes from download to working display in three steps.

**Architecture:** Three checked-in artifacts — `release/README.txt` (the full user guide), `release/setup-telemetry.bat` (auto-elevating msiexec installer), and `scripts/build-release.ps1` (publish + download pinned MSIs/licenses into a temp staging dir + zip). The zip layout is fixed by the spec's Decision 1. The batch runs `msiexec /qn` on the two MSIs (idempotent via repair/upgrade); the build script downloads them from pinned GitHub releases, never writes third-party binaries into the repo.

**Tech Stack:** Windows PowerShell 5.1 (script), cmd batch (installer), dotnet publish (single-file self-contained R2R), curl.exe (downloads), Compress-Archive (zip).

**Spec:** `docs/superpowers/specs/2026-08-11-release-bundle-design.md` (Rev 1, approved). Every decision below is copied verbatim from it.

## Global Constraints

- **Pinned versions (script constants, overridable):** LHS `LhsVersion = "0.3.4"`, PresentMon `PresentMonVersion = "2.5.1"`.
- **Download URLs:**
  - `https://github.com/epinter/LibreHardwareService/releases/download/v0.3.4/LibreHardwareService.msi`
  - `https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-v2.5.1.msi`
  - `https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-2.5.1-x64.exe`
  - `https://raw.githubusercontent.com/epinter/LibreHardwareService/main/LICENSE`
  - `https://raw.githubusercontent.com/GameTechDev/PresentMon/main/LICENSE.txt`
- **Zip layout** (Decision 1) — root folder `ModernWigiDash-win-x64\` containing: `ModernWigiDash.App.exe`, `Resources/` (publish output), `README.txt`, `setup-telemetry.bat`, `LICENSE-ModernWigiDash.txt` (repo root `LICENSE`), and `telemetry/` with `LibreHardwareService/` (MSI + LICENSE), `PresentMon/` (MSI + bootstrapper exe + LICENSE.txt), `third-party-licenses/` (NOTICES.txt + the two upstream license files).
- **`setup-telemetry.bat`** (Decision 2): auto-elevates via PowerShell UAC; runs both MSIs with `start /wait "" msiexec.exe /i "<path>" /qn /norestart`; `[OK] <name> installed` / `[FAIL] <name> - <exit code>` per component; PresentMon MSI non-zero exit → retry with `PresentMon-2.5.1-x64.exe /quiet /norestart` before reporting failure. Missing MSI → `[FAIL] ... installer not found` (exit code 2), never a raw msiexec run against a missing file. No `sc create` / `sc config` anywhere.
- **`build-release.ps1`** (Decision 3): publish command is exactly
  `dotnet publish ModernWigiDash.App\ModernWigiDash.App.csproj -c Release -r win-x64 --self-contained -o <staging>/publish -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false` (native SkiaSharp PDBs are stripped by the shared target in `Directory.Build.targets`). Downloads go to `scripts/.release-cache/`; staging is under `%TEMP%`; a failed download throws with the URL (never a partial zip); `-SkipTelemetry` builds the app-only zip (same layout minus `telemetry/`). Run from repo root, no arguments required.
- **`README.txt`** (Decision 4): plain text, Notepad-friendly, exactly the spec text (spec lines 130–214).
- **`.gitignore` collision (discovered during planning):** line 22 `[Rr]elease/` matches the checked-in `release/` templates folder. It must be negated (`!release/`, `!release/README.txt`, `!release/setup-telemetry.bat`) or the templates will never be tracked. The output zip (`ModernWigiDash-win-x64.zip`) and `scripts/.release-cache/` must also be ignored.
- **No .NET unit tests apply** — the deliverables are PowerShell/batch/README artifacts. Verification is script execution + output inspection + one real-machine acceptance run (Task 4).
- Commits: conventional prefixes, one logical change each, feature + its verification together.

---

### Task 1: `release/README.txt` (the user guide) + `.gitignore` fix

**Files:**
- Create: `release/README.txt`
- Modify: `.gitignore` (append negation + artifact rules at the end)

**Interfaces:**
- Consumes: the approved spec text (spec lines 130–214).
- Produces: `release/README.txt` — consumed by Task 3 (copied into the zip). `.gitignore` now allows `release/` to be tracked, so Task 2's `.bat` there can be committed too.

- [ ] **Step 1: Append the `.gitignore` fix**

Add this block to the end of `.gitignore`:

```gitignore
# Release bundle packaging templates (checked in; the [Rr]elease/ rule above
# would otherwise ignore this folder)
!release/
!release/README.txt
!release/setup-telemetry.bat

# Release bundle build artifacts
/ModernWigiDash-win-x64.zip
/scripts/.release-cache/
```

- [ ] **Step 2: Create `release/README.txt`**

Verbatim from the spec's Decision 4 block (spec lines 130–214). Save as UTF-8:

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

- [ ] **Step 3: Verify the `.gitignore` fix and that the file tracks**

Run:
```powershell
git check-ignore -v release/README.txt
git status --short
```
Expected: `git check-ignore` prints nothing (file is NOT ignored); `git status --short` shows `?? release/` (untracked, i.e. now trackable). If `check-ignore` still prints `release/README.txt` with the `[Rr]elease/` rule, the negation block is wrong — fix it before continuing.

- [ ] **Step 4: Commit**

```bash
git add .gitignore release/README.txt
git commit -m "docs(release): add the zip user guide (README.txt) and fix .gitignore for the release/ templates"
```

---

### Task 2: `release/setup-telemetry.bat` (auto-elevating MSI installer)

**Files:**
- Create: `release/setup-telemetry.bat`

**Interfaces:**
- Consumes: the zip layout (MSI paths below `%~dp0telemetry\...`) from Task 1's README and the spec's Decision 2.
- Produces: `release/setup-telemetry.bat` — consumed by Task 3 (copied into the zip) and exercised by Task 4.

- [ ] **Step 1: Create `release/setup-telemetry.bat`**

ASCII-only (no BOM), LF or CRLF both fine:

```bat
@echo off
setlocal EnableExtensions EnableDelayedExpansion
title ModernWigiDash - Telemetry Setup

rem ============================================================
rem  Installs the two optional telemetry services:
rem    LibreHardwareService.msi   -> LibreHardwareService service
rem    PresentMon-v2.5.1.msi      -> PresentMon Shared Service
rem  Safe to re-run (msiexec repair/upgrade semantics).
rem ============================================================

rem --- Auto-elevate: if not admin, relaunch as admin (UAC prompt) ---
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -WorkingDirectory '%~dp0' -Verb RunAs"
    exit /b 0
)

cd /d "%~dp0"

set "LHS_MSI=%~dp0telemetry\LibreHardwareService\LibreHardwareService.msi"
set "PM_MSI=%~dp0telemetry\PresentMon\PresentMon-v2.5.1.msi"
set "PM_BOOT=%~dp0telemetry\PresentMon\PresentMon-2.5.1-x64.exe"

echo.
echo Installing LibreHardwareService...
if not exist "%LHS_MSI%" (
    echo   [FAIL] LibreHardwareService - installer not found:
    echo          %LHS_MSI%
    set "LHS_RC=2"
) else (
    start /wait "" msiexec.exe /i "%LHS_MSI%" /qn /norestart
    set "LHS_RC=!errorlevel!"
)
if "!LHS_RC!"=="0" (echo   [OK] LibreHardwareService installed) else (echo   [FAIL] LibreHardwareService - msiexec exit code !LHS_RC!)

echo.
echo Installing PresentMon Shared Service...
if not exist "%PM_MSI%" (
    echo   [FAIL] PresentMon - installer not found:
    echo          %PM_MSI%
    set "PM_RC=2"
) else (
    start /wait "" msiexec.exe /i "%PM_MSI%" /qn /norestart
    set "PM_RC=!errorlevel!"
    if not "!PM_RC!"=="0" if exist "%PM_BOOT%" (
        echo   msiexec exit code !PM_RC! - retrying with the bootstrapper...
        start /wait "" "%PM_BOOT%" /quiet /norestart
        set "PM_RC=!errorlevel!"
    )
)
if "!PM_RC!"=="0" (echo   [OK] PresentMon installed) else (echo   [FAIL] PresentMon - installer exit code !PM_RC!)

echo.
if "!LHS_RC!"=="0" if "!PM_RC!"=="0" (
    echo Done. Both telemetry services are installed and start automatically.
    echo Start the app and add the Hardware Monitor / FPS + Frame Time widgets.
) else (
    echo One or both installers did not complete. Re-run this script after
    echo resolving the problem, or see the Troubleshooting section of README.txt.
)
echo.
pause
endlocal
```

Why the specifics (do not "simplify" them away):
- `start /wait "" msiexec.exe ...` — msiexec is a GUI-subsystem app; a bare call returns immediately and `%errorlevel%` is meaningless. `start /wait` makes the batch block until the install finishes and propagates the exit code.
- `EnableDelayedExpansion` + `!var!` — the `else ( ... )` blocks are parsed as one unit, so `%var%` inside them would expand to the pre-block value (empty on first run). Only `!var!` sees the freshly-set value.
- The missing-installer guard makes the batch fail with a readable message (not a silent msiexec error) when run before Task 3 has built a zip — this is what makes the batch testable without a real install.
- `exit /b 0` after launching the elevated copy so the original non-admin window closes cleanly.

- [ ] **Step 2: Syntax + elevation smoke test (no MSIs needed)**

From a **non-elevated** PowerShell:
```powershell
cmd /c "release\setup-telemetry.bat"
```
Expected: prints `Requesting administrator rights...` and triggers a UAC prompt (cancel it — we only want the branch to fire). If the shell is already elevated, `net session` succeeds and the script instead reaches the "Installing ..." section, where it must print `[FAIL] <name> - installer not found` for both components (exit code 2) and `pause`. Either outcome proves the control flow and the missing-file guard.

- [ ] **Step 3: Verify the batch stays trackable, then commit**

```powershell
git check-ignore -v release/setup-telemetry.bat   # expect no output
```
```bash
git add release/setup-telemetry.bat
git commit -m "feat(release): add auto-elevating setup-telemetry.bat that installs the bundled telemetry MSIs
```

---

### Task 3: `scripts/build-release.ps1` (publish + download + zip)

**Files:**
- Create: `scripts/build-release.ps1`

**Interfaces:**
- Consumes: Task 1's `release/README.txt`, Task 2's `release/setup-telemetry.bat`, repo-root `LICENSE`, `ModernWigiDash.App\ModernWigiDash.App.csproj`, the publish output shape (single `ModernWigiDash.App.exe` + `Resources/` folder — PDBs already stripped by `Directory.Build.targets`).
- Produces: `ModernWigiDash-win-x64.zip` at the repo root with the exact Decision-1 layout; `scripts/.release-cache/` holding the pinned downloads (already gitignored by Task 1's block).

- [ ] **Step 1: Create `scripts/build-release.ps1`**

```powershell
[CmdletBinding()]
param(
    [switch]$SkipTelemetry,
    [string]$LhsVersion = "0.3.4",
    [string]$PresentMonVersion = "2.5.1",
    [string]$OutputZip = "ModernWigiDash-win-x64.zip"
)

$ErrorActionPreference = "Stop"

$Root       = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ReleaseDir = Join-Path $Root "release"
$CacheDir   = Join-Path $Root "scripts\.release-cache"
$Staging    = Join-Path ([System.IO.Path]::GetTempPath()) "wmd-release-staging"
$ZipDir     = Join-Path $Staging "ModernWigiDash-win-x64"
$ZipPath    = Join-Path $Root $OutputZip

$LhsRelease = "https://github.com/epinter/LibreHardwareService/releases/download/v$LhsVersion"
$PmRelease  = "https://github.com/GameTechDev/PresentMon/releases/download/v$PresentMonVersion"

$Downloads = [ordered]@{
    "LibreHardwareService.msi"             = "$LhsRelease/LibreHardwareService.msi"
    "PresentMon-v$PresentMonVersion.msi"   = "$PmRelease/PresentMon-v$PresentMonVersion.msi"
    "PresentMon-$PresentMonVersion-x64.exe" = "$PmRelease/PresentMon-$PresentMonVersion-x64.exe"
    "LHS-LICENSE.txt"                      = "https://raw.githubusercontent.com/epinter/LibreHardwareService/main/LICENSE"
    "PresentMon-LICENSE.txt"               = "https://raw.githubusercontent.com/GameTechDev/PresentMon/main/LICENSE.txt"
}

function Get-Download([string]$Url, [string]$Dest) {
    if (Test-Path -LiteralPath $Dest) { Write-Host "  cached: $(Split-Path $Dest -Leaf)"; return }
    Write-Host "  downloading $(Split-Path $Dest -Leaf)..."
    & curl.exe -f -L -sS -o "$Dest" "$Url"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Dest)) {
        throw "Download failed: $Url"
    }
}

# --- 1. Publish the app (single-file, self-contained, R2R, no PDBs) ---
Write-Host "Publishing ModernWigiDash.App (single-file, self-contained)..."
$publishOut = Join-Path $Staging "publish"
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Path $Staging -Force | Out-Null
& dotnet publish (Join-Path $Root "ModernWigiDash.App\ModernWigiDash.App.csproj") `
    -c Release -r win-x64 --self-contained -o $publishOut `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# --- 2. Assemble the zip root ---
New-Item -ItemType Directory -Path $ZipDir -Force | Out-Null

# App payload: the single-file exe + the Resources folder (fonts, theme, icons)
Copy-Item (Join-Path $publishOut "ModernWigiDash.App.exe") $ZipDir
Copy-Item (Join-Path $publishOut "Resources") (Join-Path $ZipDir "Resources") -Recurse

# --- 3. Telemetry bundle (unless skipped) ---
if (-not $SkipTelemetry) {
    Write-Host "Downloading telemetry installers + licenses..."
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    $lhsDir = Join-Path $ZipDir "telemetry\LibreHardwareService"
    $pmDir  = Join-Path $ZipDir "telemetry\PresentMon"
    $licDir = Join-Path $ZipDir "telemetry\third-party-licenses"
    New-Item -ItemType Directory -Path $lhsDir, $pmDir, $licDir -Force | Out-Null

    foreach ($name in $Downloads.Keys) {
        $cached = Join-Path $CacheDir $name
        Get-Download -Url $Downloads[$name] -Dest $cached
    }

    Copy-Item (Join-Path $CacheDir "LibreHardwareService.msi") $lhsDir
    Copy-Item (Join-Path $CacheDir "PresentMon-v$PresentMonVersion.msi") $pmDir
    Copy-Item (Join-Path $CacheDir "PresentMon-$PresentMonVersion-x64.exe") $pmDir
    Copy-Item (Join-Path $CacheDir "LHS-LICENSE.txt") $lhsDir
    Copy-Item (Join-Path $CacheDir "PresentMon-LICENSE.txt") $pmDir
    Copy-Item (Join-Path $CacheDir "LHS-LICENSE.txt") (Join-Path $licDir "LHS-LICENSE.txt")
    Copy-Item (Join-Path $CacheDir "PresentMon-LICENSE.txt") (Join-Path $licDir "PresentMon-LICENSE.txt")

    @"
ModernWigiDash redistributes the following third-party components:

1. LibreHardwareService  v$LhsVersion
   License: MPL-2.0
   Source:  https://github.com/epinter/LibreHardwareService
   Files:   LibreHardwareService.msi, LICENSE (in LibreHardwareService/)

2. PresentMon Service    v$PresentMonVersion
   License: MIT
   Source:  https://github.com/GameTechDev/PresentMon
   Files:   PresentMon-v$PresentMonVersion.msi, PresentMon-$PresentMonVersion-x64.exe,
            LICENSE.txt (in PresentMon/)

Full license texts are included next to each component and in this folder.
MPL-2.0 requires that corresponding source be made available; both components'
source is public at the URLs above.
"@ | Set-Content -Path (Join-Path $licDir "NOTICES.txt") -Encoding UTF8
}

# --- 4. Templates ---
Copy-Item (Join-Path $ReleaseDir "README.txt") $ZipDir
Copy-Item (Join-Path $ReleaseDir "setup-telemetry.bat") $ZipDir
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $ZipDir "LICENSE-ModernWigiDash.txt")

# --- 5. Zip (root folder included inside) ---
Write-Host "Zipping..."
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path $ZipDir -DestinationPath $ZipPath
if (-not (Test-Path $ZipPath)) { throw "Zip creation failed" }

# --- 6. Contents manifest + size ---
Write-Host ""
Write-Host "Built $ZipPath"
Get-ChildItem $ZipDir -Recurse -File | ForEach-Object {
    "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($ZipDir.Length + 1)
}
Write-Host ""
Write-Host ("Zip size: {0:N1} MB" -f ((Get-Item $ZipPath).Length / 1MB))
```

- [ ] **Step 2: Run the app-only path first (fast, no network)**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -SkipTelemetry
```
Expected: `dotnet publish` succeeds (single-file R2R, no `libSkiaSharp.pdb` in output — the `Directory.Build.targets` trim targets handle it); script prints the manifest; zip written to the repo root. If `dotnet publish` fails, stop and fix the publish command before continuing (it was previously verified in commit `ae3a748`).

- [ ] **Step 3: Inspect the app-only zip layout**

```powershell
Expand-Archive ModernWigiDash-win-x64.zip -DestinationPath "$env:TEMP\wmd-zip-check" -Force
Get-ChildItem "$env:TEMP\wmd-zip-check\ModernWigiDash-win-x64" -Recurse | ForEach-Object { $_.FullName.Substring($env:TEMP.Length) }
```
Expected: `ModernWigiDash-win-x64\ModernWigiDash.App.exe`, `ModernWigiDash-win-x64\Resources\` (with Fonts/Logo contents), `README.txt`, `setup-telemetry.bat`, `LICENSE-ModernWigiDash.txt`. **No** `telemetry\` folder. Confirm `README.txt` in the zip is byte-identical to `release/README.txt` and `setup-telemetry.bat` to `release/setup-telemetry.bat`.

- [ ] **Step 4: Confirm git stays clean, then commit**

```powershell
git status --short
```
Expected: only `scripts/build-release.ps1` and `.gitignore` staged/untracked — the zip and `scripts/.release-cache\` (created only on the full run) must NOT appear (Task 1's ignore rules). Delete `$env:TEMP\wmd-zip-check` and the repo-root zip before committing? No — the zip is ignored, leave it for Task 4. Commit:
```bash
git add scripts/build-release.ps1 .gitignore
git commit -m "feat(release): add build-release.ps1 that publishes the app and assembles the distribution zip
```

---

### Task 4: Full end-to-end acceptance (real machine, needs display + admin)

**Files:** none (verification only).

**Interfaces:** exercises everything Task 1–3 produced, against the spec's Verification section (spec lines 231–244).

> **Execution environment requirement:** this task runs on the real machine with the WigiDash display attached and needs an admin prompt for the MSI installs. The MSI installs over the already-running LHS/PresentMon v2.5.1/v0.3.4 as a repair — the intended shipping path. Do not skip steps.

- [ ] **Step 1: Full build (downloads the ~218 MB of MSIs + bootstrapper + licenses)**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
```
Expected: publish succeeds; downloads land in `scripts\.release-cache\`; manifest shows:
- `ModernWigiDash.App.exe`, `Resources\*`, `README.txt`, `setup-telemetry.bat`, `LICENSE-ModernWigiDash.txt`
- `telemetry\LibreHardwareService\LibreHardwareService.msi` + `LICENSE`
- `telemetry\PresentMon\PresentMon-v2.5.1.msi` + `PresentMon-2.5.1-x64.exe` + `LICENSE.txt`
- `telemetry\third-party-licenses\` with `NOTICES.txt`, `LHS-LICENSE.txt`, `PresentMon-LICENSE.txt`
Zip ≈ 230 MB. If a download 404s, curl's `-f` makes the script throw with the URL — verify the pinned release tags still match upstream.

- [ ] **Step 2: Extract to a path with spaces**

```powershell
New-Item -ItemType Directory "C:\Users\Public\WigiDash Test Folder" -Force
Expand-Archive ModernWigiDash-win-x64.zip -DestinationPath "C:\Users\Public\WigiDash Test Folder" -Force
```

- [ ] **Step 3: Run the installer batch**

```powershell
cmd /c "C:\Users\Public\WigiDash Test Folder\ModernWigiDash-win-x64\setup-telemetry.bat"
```
(expect the UAC elevation prompt, confirm it) → Expected: `[OK] LibreHardwareService installed`, `[OK] PresentMon installed`, and the "Done." summary.

- [ ] **Step 4: Verify both services are Running / Automatic**

```powershell
sc.exe query LibreHardwareService
sc.exe query PresentMonSharedService
sc.exe qc PresentMonSharedService
```
Expected: `STATE: 4 RUNNING`, `START_TYPE: 2 AUTO_START` for both.

- [ ] **Step 5: Idempotency — re-run the batch**

Re-run the Step 3 command. Expected: both `[OK]` again (msiexec repair), no errors.

- [ ] **Step 6: App + widgets live**

With the display attached, double-click `ModernWigiDash.App.exe` in the extracted folder. Expected: display shows widgets; the Hardware Monitor widget shows live CPU/GPU/VRAM values and the FPS / Frame Time widget shows a tracked process (both widgets' "unavailable" state must NOT appear).

- [ ] **Step 7: Uninstall path**

Settings → Apps → Installed apps → uninstall "LibreHardwareService" and "Intel PresentMon", then delete `C:\Users\Public\WigiDash Test Folder`. (Restore the prior install state afterward if the user wants to keep using telemetry: re-run `setup-telemetry.bat` from the zip, or reinstall via the vendor installers.)

- [ ] **Step 8: App-only zip sanity (already covered in Task 3)**

Confirm the Task 3 `-SkipTelemetry` zip is still present and valid (re-extract once to `$env:TEMP` and check the manifest line for `telemetry\` is absent). No commit needed.

---

## Self-Review

- **Spec coverage:** Decision 1 (layout) → Task 3 Steps 1/3. Decision 2 (batch) → Task 2. Decision 3 (build script) → Task 3. Decision 4 (README) → Task 1. Error handling (bootstrapper fallback, missing-installer guard, download-throw, `-SkipTelemetry`) → Task 2 Step 1 + Task 3 Step 1. Verification section → Task 4 (all six items: full build, manifest, path-with-spaces + services + idempotent re-run, live widgets, uninstall, `-SkipTelemetry`). Out-of-scope items (CI workflow, signed driver, auto-start) → deliberately not implemented.
- **Placeholder scan:** every step carries exact file content or an exact command + expected output. No "add error handling"/"similar to Task N" anywhere.
- **Type consistency:** `setup-telemetry.bat` MSI paths match the build script's copy destinations (`telemetry\LibreHardwareService\LibreHardwareService.msi`, `telemetry\PresentMon\PresentMon-v2.5.1.msi`, `telemetry\PresentMon\PresentMon-2.5.1-x64.exe`); the bootstrapper filename matches the pinned asset name; README's uninstall/service-name text matches the verified live install (`PresentMonSharedService`, "Intel PresentMon").
