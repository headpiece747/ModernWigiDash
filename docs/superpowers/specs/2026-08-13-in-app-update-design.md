# In-App Auto-Update — Design

- **Date:** 2026-08-13
- **Status:** Approved (pending implementation plan)
- **Visual mockup:** update-button-states.html (approved; button left of Snap to Grid, three states, Griddy icons, hover tooltips)

## Goal

An in-app update flow for ModernWigiDash: an update button in the header (left of
⚡ Snap to Grid) appears when a newer GitHub release exists, downloads the update
on click, stages it, and applies it on app restart — in the same location the
user has the app installed, preserving their profile and theme.

## Decisions

| Decision | Choice |
|----------|--------|
| Apply flow | Download now, apply on restart (never overwrite a running exe). |
| Version source | Build-time stamp (`AssemblyInformationalVersion`) written by `build-release.ps1` from the release tag. |
| Check cadence | Once at startup (silent + logged on failure; hidden when up-to-date/offline). |
| Swap executor | Bundled `apply-update.cmd` (batch — immune to PowerShell execution policy) embedded as an app resource. |
| Update artifact | Slim **app-only zip** (exe + Resources + README + LICENSE, ~100 MB vs 294 MB full). |
| Upstream services | Auto-resolve latest LHS/PresentMon at release-build time (overridable pins), versions recorded in the zip. |
| Icons | Real Griddy SVG paths parsed to WPF geometry (`Geometry.Parse` on `GriddyIconPaths` map, cached). |
| Stale stages | Cleaned at startup; interrupted swaps recovered (`.old` restore). |

## Architecture

### Version stamping (`build-release.ps1`)

- `build-release.ps1 -Version X` already stamps README.txt; it now also passes
  `InformationalVersion` to the App publish so the exe embeds `X`.
- Dev builds (no `-Version`) embed `0.0.0-dev`; the updater treats `0.0.0` /
  unparseable versions as "no update possible" (dev binaries never nag).

### Update checker (`ModernWigiDash.App/Update/UpdateChecker.cs` + `UpdateService.cs`)

- `UpdateService` runs once at startup on a background thread:
  `GET https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest`
  (10s timeout), parse `tag_name` + assets (name, browser_download_url, digest).
- Pure `UpdateChecker` logic (testable via `StubHttpHandler`):
  - SemVer-compare latest tag vs. current version → update available?
  - Pick the **slim asset** `ModernWigiDash-v{X}-app-only.zip` (never the full zip).
  - Prereleases/drafts are excluded automatically by the API.
  - Failure → silent (log line only), button stays hidden.

### Release pipeline (`build-release.ps1` + upstream auto-resolve)

- The full `ModernWigiDash-v{X}-win-x64.zip` stays the canonical fresh-install
  artifact (unchanged).
- The script also produces `ModernWigiDash-v{X}-app-only.zip`:
  exe, `Resources/`, `README.txt`, `LICENSE-ModernWigiDash.txt`. **Excluded:**
  `telemetry/` and `setup-telemetry.bat` (fresh-install only).
- **Upstream auto-resolve:** when `-LhsVersion` / `-PresentMonVersion` are not
  explicitly pinned, query each upstream GitHub `releases/latest` and bundle the
  newest stable. Pins remain for reproducibility. Resolved versions are written
  to `telemetry-versions.txt` inside the zip (and to the release notes).
- `release.yml` unchanged (already runs `build-release.ps1 -Version` and uploads
  produced assets).
- **Risk (verified during implementation):** the app loads PresentMonAPI2.dll
  from the service dir — client↔service binary protocol isn't backward-guaranteed
  (issue #383). The app's `PresentMonApiProbe` already degrades gracefully on
  version mismatch; confirm that path still holds with an auto-bumped PresentMon.

### Download + stage (`ModernWigiDash.App/Update/UpdateService.cs`)

On the amber-button click:
1. Download the slim zip to `%LOCALAPPDATA%\ModernWigiDash\updates\` with
   progress reporting (tooltip shows %).
2. **Verify SHA-256** against the GitHub API asset digest before extracting.
   On mismatch: delete the download, revert to hidden/silent-failed state.
3. Extract to `updates\staged\{version}\`.
4. Write `apply-update.cmd` (embedded resource) into the staged folder.
5. Button flips to the green refresh state.

### `apply-update.cmd` — the swap (embedded app resource)

Spawned hidden on "Restart now"; the app then closes normally (standby teardown).

1. Wait for **all** `ModernWigiDash.App` processes to exit (tasklist loop,
   ~60s timeout) — not just the spawned PID (second instances).
2. Retry loop on file-in-use (10 × 1s) for AV/scan delays.
3. **Writability check** on the install dir first; if not writable, self-elevate
   via PowerShell's `Start-Process -Verb RunAs` (the `RunAs` verb is a Win32
   launch — it does not execute a PowerShell script, so execution policy is
   irrelevant here; the batch itself stays the executor). Standard accounts that
   cannot elevate get a clear "run as admin" message.
4. **Rename-aside swap** (never delete-first — crash-safe):
   `exe → exe.old` → copy staged exe + Resources into install dir → delete
   `exe.old` only after the new exe is verified in place.
5. Preserve unknown/user files (`app_theme.json` etc. — only known app files are
   replaced/deleted).
6. Relaunch the app from the install dir, delete the staged folder, exit.
7. Log to `%LOCALAPPDATA%\ModernWigiDash\updates\update.log`.

### Startup recovery (in-app)

On launch: if `exe.old` exists → the swap was interrupted — restore it if the
new exe is missing, else delete it. Stale staged folders are deleted.

### UI (`ModernWigiDash.App/MainWindow.xaml` + partials)

- New button left of `ChkSnapToGrid` in the header StackPanel. Hidden by default.
- Three states (approved mockup):
  - **Amber `arrow-circle-down`** — update available; tooltip `Update v0.5.0 available`.
  - **`swap-horizontal` + spinner** — downloading; tooltip `Downloading v0.5.0… 47%` (click disabled).
  - **Green `refresh`** — staged; tooltip `Restart to apply`.
- Click amber → start download. Click green → restart prompt dialog:
  *"Update ready — restart to apply. v0.5.0 is downloaded and staged. It will be
  installed in place when the app closes. Your profile and theme are preserved."*
  — **Later** / **Restart now**.
- Restart now → spawn `apply-update.cmd` hidden → close the app.
- Icons: `GriddyIconPaths.g.cs` path data parsed to WPF `Geometry` via
  `Geometry.Parse`, cached per icon name (`arrow-circle-down`, `swap-horizontal`,
  `refresh` — names verified present). Drawn in the button via a WPF `Path`.
  The parse+cache lives in `ModernWigiDash.App/Update/GriddyIconGeometry.cs`
  (static, testable: parse-once, case-insensitive keying, empty-path fallback).

## Data Flow

1. Startup → `UpdateService.CheckAsync()` → GitHub API → newer? → show amber.
2. Click amber → download slim zip (progress) → SHA-256 verify → extract to stage
   → write `.cmd` → green state.
3. Click green / "Restart now" → spawn `.cmd` hidden → app exits (standby).
4. `.cmd` waits for exit → writability/elevation → rename-aside swap → relaunch.
5. Next startup → stale-stage cleanup + `.old` recovery.

## Error Handling

- GitHub API failure / offline / rate limit → silent, logged, button hidden.
- Corrupt download (SHA-256 mismatch) → deleted, silent, logged.
- Non-writable install dir → `.cmd` self-elevates; uncapable → clear message.
- Swap interrupted mid-way → `.old` restore at next startup.
- Dev builds (`0.0.0`) → updater disabled.

## Testing

- `UpdateChecker` (pure): SemVer compare, JSON parse, slim-asset pick, digest
  passthrough — via `StubHttpHandler`/`FakeTimeProvider` seams.
- `UpdateService`: download-progress, SHA-256 verify (good + corrupt), stage
  naming, stale-stage cleanup, `.old` recovery — via seams.
- `apply-update.cmd`: integration test on a temp install dir (fake exe +
  Resources) driving the real script: rename-aside, lock-retry, preserve-user-
  files, relaunch.
- `build-release.ps1`: slim-zip + upstream auto-resolve exercised in a local run
  before the next release.
- `GriddyIconGeometry`: parse + cache + the three icon names resolve.
- Existing 993 tests stay green; render/transport path untouched.

## Out of Scope (YAGNI)

- No periodic re-check while running (startup only, per decision).
- No delta updates / differential download (slim zip is the size win).
- No rollback beyond `.old` recovery (no multiple-version journal).
- No signing / SmartScreen bypass (README documents "Run anyway").
- No updater for the Windows Service (it was removed, ADR-0005).
