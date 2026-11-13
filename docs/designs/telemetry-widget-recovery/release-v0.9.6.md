# Release v0.9.6

Status: prepared (awaiting release commit & tag v0.9.6)

## Target

Release tag `v0.9.6` on the release commit (not yet committed or tagged).

## Artifacts

- `ModernWigiDash-v0.9.6-win-x64.zip` (full release bundle: single-file `ModernWigiDash.App.exe`, `LICENSE-ModernWigiDash.txt`, `README.txt`, `Resources\Fonts\Geist-VariableFont_wght.ttf`, `Resources\Logo\logo.ico`, `setup-telemetry.bat`, `telemetry/` offline MSIs + `telemetry-versions.txt`, `NOTICES.txt` + license files)
- `ModernWigiDash-v0.9.6-app-only.zip` (slim in-app updater payload: single-file `ModernWigiDash.App.exe`, `LICENSE-ModernWigiDash.txt`, `README.txt`, `Resources\Fonts\Geist-VariableFont_wght.ttf`, `Resources\Logo\logo.ico`)

## What changed

- **Media session & artwork concurrency safety (`ModernWigiDash.Widgets`)**:
  - `MediaSessionMonitor.cs`: Added `_stateLock` and `_snapshotPublishLock` synchronization without holding locks across COM/`Dispose()`/`SnapshotChanged`, disposed late managers when `DisposeAsync` races `InitializeAsync`, disposed single/zero-session fresh wrappers in `CycleSession`, made event subscription non-throwing (`TrySubscribeSessionEvents`), guarded both session and manager `-=` teardown with `SafeDetachEvent` so `SafeDisposeSession`/`SafeDisposeManager` always run, and suppressed post-dispose notifications.
  - `ArtworkLoader.cs`: Added `_lock` around artwork state/versioning, cleared `_loadingArtworkKey` only for the matching active version (`version == _artLoadVersion`), extracted background color from the thread-local decoded bitmap before publishing `_albumArt`, and disposed late/superseded bitmaps outside `_lock` so render-thread `DisposeRetired()` cannot race `ExtractBackgroundColor`.
- **PresentMon introspection, query & catalog safety (`ModernWigiDash.App\PresentMon\`)**: Guarded `IntPtr.Zero` pointers (`rootPtr`, `root.pMetrics`, `pData`, null metric slots), deduplicated metric IDs in `PresentMonMetricCatalog`, and freed the dynamic query handle (`Free()`) in `PresentMonQueryRegistry` when frame query registration fails or returns an invalid blob size.
- **In-app updater, vendor cache, and single-instance hardening**:
  - `ModernWigiDash.App\Update\apply-update.cmd`: Normalized trailing backslashes on `%INSTALL%` and `%STAGE%`, unconditionally cleared any stale `.exe.old` before moving the live executable aside, added a bounded retry/error check around `Resources` `xcopy`, added an `[elevated]` (`%~4`) recursion guard exiting `5`, forwarded caller `%LOG%` across UAC via `%~5`, and rewrote `:elevate` to pass paths through `WMD_CMD`/`WMD_INST`/`WMD_STG`/`WMD_EXE`/`WMD_LOG` environment variables and PowerShell quoting with `$ErrorActionPreference = 'Stop'`.
  - `ModernWigiDash.App\Update\UpdateService.cs`: Trimmed trailing separators in `LaunchUpdater`, disposed `HttpRequestMessage` in `DownloadTextAsync` without duplicating `User-Agent`, cleaned up `downloadDir` and `stagedDir` on failure, and used `overwriteFiles: true` in `ExtractSlimZip`.
  - `ModernWigiDash.Hardware\Service\WigiDashServiceClient.cs`: Coalesced null `VendorSensorItem.Name` in `ProjectSensors` (alongside `HwinfoWidget` and `VendorSensorRefresh`), logged exceptions in `RefreshCache`, cleared cached sensor list, cached readings, and requested keys in `PublishSnapshot` on `fetchFailed`, while preserving keys added mid-refresh by other widgets on success (`_requestedKeys.Remove(key)` for `requested` only).
  - `ModernWigiDash.App\SingleInstanceGuard.cs`: Added `Dispose()` cleanup if primary `CreateEvent()` or `ParkActivationWait()` throws after claiming the mutex, and immediately disposed secondary-instance `Mutex` when `!createdNew`.
- **Hotkey ID range & P/Invoke signature (`ModernWigiDash.App\Hotkey\`)**:
  - `GlobalHotkeyManager.cs`: Allocated IDs within the Win32 `RegisterHotKey` application range `1..0xBFFF` while skipping live `_registered` IDs on wraparound (`AllocateId()`).
  - `HotkeyApi.cs`: Removed the extraneous 3rd `ushort vk` argument from `UnregisterHotKeyFn` / `UnregisterHotKeyPInvoke` and matched native ABI types (`uint vk`, `[return: MarshalAs(UnmanagedType.Bool)] bool` with `SetLastError = true`).
- **Release packaging, CI, git hooks, and user copy**:
  - `ModernWigiDash.App\PresentMon\PresentMonNative.cs`: Added `try/catch` cleanup in `OpenSession` so a throwing `_queryRegistry.Register` closes `_session` instead of leaving a half-initialized handle.
  - `.github\workflows\release.yml`: Added `$LASTEXITCODE` checks after quality gates, `build-release.ps1`, `gh release upload`/`create`/`view`, and release-note credit prepending.
  - `scripts\build-release.ps1`: Case-insensitive XAML version pre-flight check, version-keyed `.release-cache` subdirectories (`lhs-$LhsVersion`, `pm-$PresentMonVersion`), corrected `LHS-LICENSE.txt` / `PresentMon-LICENSE.txt` names in `NOTICES.txt`, and expanded `5d` sandbox verification for both full and app-only zip payloads.
  - `release\setup-telemetry.bat`: Environment-variable UAC relaunch, accepted MSI exit code `3010` alongside `0` with explicit code-3010 reboot messaging, non-zero exit (`exit /b 1`) on failure, and corrected `"Hardware Monitor and FPS / Frame Time"`; `.gitattributes` enforces `*.bat text eol=crlf`.
  - `scripts\scan-staged-cr.ps1` and `scripts\scan-staged-userdata.ps1`: Added `-c core.quotePath=false`, fixed EOF lone-CR detection, added case-insensitive user-data basename checks, and updated Pester tests.
  - Updated UI hints (`SystemTelemetryPresentation`, `HwinfoWidget`, `AidaPanelCanvas`, `InspectorPanelRenderer`), `release\README.txt`, `README.md`, `CONTEXT.md`, and `docs\adr\0025-vendor-reads-from-a-background-cache.md`.

## Procedure

Owner: `.github/workflows/release.yml` on annotated tag push (no release created, edited, or deleted by hand).

1. Run `powershell -NoProfile -File scripts\ps-hygiene.ps1` and `powershell -NoProfile -File scripts\run-gates.ps1 -Label "v0.9.6 release-ready"` within the 60-minute gate freshness window (`scripts\gate-guard.ps1`).
2. Verify `scripts\build-release.ps1 -Version 0.9.6` locally (without `-Clean`) and smoke-test the extracted single-file `ModernWigiDash.App.exe`.
3. Commit release-ready changes, create annotated tag `v0.9.6` (`git rev-parse HEAD` == `git rev-list -n1 v0.9.6`), push `master` and tag `v0.9.6` so `.github/workflows/release.yml` builds and publishes both zips to GitHub Releases.

## Pre-flight evidence

- `scripts\ps-hygiene.ps1`: `17` `.ps1` files pure ASCII, analyzer clean, `33/33` Pester tests passed (`GateGuard.Tests.ps1`, `ParseRegression.Tests.ps1`, `RefCheck.Tests.ps1`, `ScanStagedCr.Tests.ps1`, `ScanStagedUserData.Tests.ps1`), `0` failed, `0` skipped.
- `scripts\run-gates.ps1 -Label "v0.9.6 release-ready"`: `0` build warnings, `0` errors, `3070/3070` tests passed (`0` failed), `dotnet format` clean; `scripts\gate-guard.ps1` allowed commit (`2026-11-13T23:44:41Z`, `369f6f4`, `0` min old).
- `scripts\build-release.ps1 -Version 0.9.6` (using version-keyed cache under `scripts\.release-cache`):
  - Telemetry bundled (`telemetry-versions.txt`): `LibreHardwareService=0.3.4`, `PresentMon=2.6.0`, with `LHS-LICENSE.txt`, `PresentMon-LICENSE.txt`, and `NOTICES.txt` pointing to `https://github.com/epinter/LibreHardwareService` and `https://github.com/GameTechDev/PresentMon`.
  - `ModernWigiDash-v0.9.6-win-x64.zip`: `342,802,021` bytes (`326.9 MB`), including single-file `ModernWigiDash.App.exe` (`120,957,415` bytes).
  - `ModernWigiDash-v0.9.6-app-only.zip`: `114,202,748` bytes (`108.9 MB`, `3.00x`).
  - Sandbox verification: `Release package sandbox smoke verification PASSED.`

## Post-release checks

- Verify `gh release view v0.9.6 --json isDraft,isPrerelease,author,assets`: `isDraft=false`, `isPrerelease=false`, `author=github-actions[bot]`, and `releases/latest` resolves to `v0.9.6`.
- Verify both `ModernWigiDash-v0.9.6-win-x64.zip` and `ModernWigiDash-v0.9.6-app-only.zip` carry `sha256:` digests and exact byte sizes matching the CI run log's `verified <name>: N bytes` lines (note: CI `Compress-Archive` zip hashes differ from local builds by timestamp metadata).
- Confirm release notes carry generated PR/commit notes and the ElmorLabs credit (`--generate-notes` + workflow header), and confirm `telemetry/third-party-licenses/telemetry-versions.txt` in the downloaded full zip.

## End-to-end update (from the published v0.9.5 payload)

- Install/run `v0.9.5` and apply the `v0.9.6` update through the in-app updater (`Restart to apply`).
- Inspect `%LOCALAPPDATA%\ModernWigiDash\updates\update.log` across the single `v0.9.5 -> v0.9.6` restart and record **both counts** below:
  - Per-user install `updater start args:` count: `(pending)`
  - Elevated install `swap complete` count: `(pending)`

## Rollback

If a defect ships in `v0.9.6`, do **not** delete the release and do **not** move the `v0.9.6` tag; ship a forward fix release (`v0.9.7`).

## Post-release result

(pending)
