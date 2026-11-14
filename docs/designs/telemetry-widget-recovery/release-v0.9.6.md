# Release v0.9.6

Status: released (CI, GitHub release, per-user E2E, and elevated E2E verified)

## Target

Release tag `v0.9.6` at `f01062f` (`tag object 8cb494cc91db6e3c44e8bcbf27ac453d81421f4d`).

## Artifacts

- `ModernWigiDash-v0.9.6-win-x64.zip` (full release bundle: single-file `ModernWigiDash.App.exe`, `LICENSE-ModernWigiDash.txt`, `README.txt`, `Resources\Fonts\Geist-VariableFont_wght.ttf`, `Resources\Logo\logo.ico`, `setup-telemetry.bat`, `telemetry/` offline MSIs + `telemetry-versions.txt`, `NOTICES.txt` + license files)
- `ModernWigiDash-v0.9.6-app-only.zip` (slim in-app updater payload: single-file `ModernWigiDash.App.exe`, `LICENSE-ModernWigiDash.txt`, `README.txt`, `Resources\Fonts\Geist-VariableFont_wght.ttf`, `Resources\Logo\logo.ico`)

## What changed

- **Media session & artwork concurrency safety (`ModernWigiDash.Widgets`)**:
  - `MediaSessionMonitor.cs`: Added `_stateLock` and `_snapshotPublishLock` synchronization without holding `_stateLock` across COM/`Dispose()`/`SnapshotChanged` (serializing `SnapshotChanged` under `_snapshotPublishLock`), disposed late managers when `DisposeAsync` races `InitializeAsync`, disposed single/zero-session fresh wrappers in `CycleSession`, made event subscription non-throwing (`TrySubscribeSessionEvents`), guarded both session and manager `-=` teardown with `SafeDetachEvent` so `SafeDisposeSession`/`SafeDisposeManager` always run, and suppressed post-dispose notifications.
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
  - `scripts\build-release.ps1`: Case-insensitive XAML version pre-flight check, version-keyed `.release-cache` subdirectories (`lhs-$LhsVersion`, `pm-$PresentMonVersion`), corrected `LHS-LICENSE.txt` / `PresentMon-LICENSE.txt` names in `NOTICES.txt`, `$fileVer` normalization for unpacked `FileVersion` checks, and expanded `5d` sandbox verification for both full and app-only zip payloads.
  - `release\setup-telemetry.bat`: Environment-variable UAC relaunch, accepted MSI exit code `3010` alongside `0` with explicit code-3010 reboot messaging, non-zero exit (`exit /b 1`) on failure, and corrected `"Hardware Monitor and FPS / Frame Time"`; `.gitattributes` enforces `*.bat text eol=crlf`.
  - `scripts\scan-staged-cr.ps1` and `scripts\scan-staged-userdata.ps1`: Added `-c core.quotePath=false`, fixed EOF lone-CR detection, added case-insensitive user-data basename checks, and updated Pester tests.
  - `ModernWigiDash.Tests\`: Replaced ThreadPool/wall-clock timing races with deterministic thread handshakes and `TestWait.WaitUntilAsync` polling across `DisplayWireTests`, `ArtworkLoaderTests`, `WigiDashServiceClientTests`, `MediaSessionMonitorTests`, `AidaPanelMasterTests`, `AidaPanelServiceTests`, and `SingleInstanceGuardTests` for 2-vCPU CI runners.
  - Updated UI hints (`SystemTelemetryPresentation`, `HwinfoWidget`, `AidaPanelCanvas`, `InspectorPanelRenderer`), `release\README.txt`, `README.md`, `CONTEXT.md`, and `docs\adr\0025-vendor-reads-from-a-background-cache.md`.

## Procedure

Owner: `.github/workflows/release.yml` on annotated tag push (no release created, edited, or deleted by hand).

1. Run `powershell -NoProfile -File scripts\ps-hygiene.ps1` and `powershell -NoProfile -File scripts\run-gates.ps1 -Label "v0.9.6 release-ready"` within the 60-minute gate freshness window (`scripts\gate-guard.ps1`).
2. Verify `scripts\build-release.ps1 -Version 0.9.6` locally (without `-Clean`) and smoke-test the extracted single-file `ModernWigiDash.App.exe`.
3. Commit release-ready changes, create annotated tag `v0.9.6` (`git rev-parse HEAD` == `git rev-list -n1 v0.9.6`), push `master` and tag `v0.9.6` so `.github/workflows/release.yml` builds and publishes both zips to GitHub Releases.

## Pre-flight evidence

- `scripts\ps-hygiene.ps1`: `17` `.ps1` files pure ASCII, analyzer clean, `33/33` Pester tests passed (`GateGuard.Tests.ps1`, `ParseRegression.Tests.ps1`, `RefCheck.Tests.ps1`, `ScanStagedCr.Tests.ps1`, `ScanStagedUserData.Tests.ps1`), `0` failed, `0` skipped.
- `scripts\run-gates.ps1 -Label "v0.9.6 release-ready"`: `0` build warnings, `0` errors, `3070/3070` tests passed (`0` failed), `dotnet format` clean; `scripts\gate-guard.ps1` allowed commit (`2026-11-14T00:37:55Z`, `498696c`).
- `scripts\build-release.ps1 -Version 0.9.6` (using version-keyed cache under `scripts\.release-cache`):
  - Telemetry bundled (`telemetry-versions.txt`): `LibreHardwareService=0.3.4`, `PresentMon=2.6.0`, with `LHS-LICENSE.txt`, `PresentMon-LICENSE.txt`, and `NOTICES.txt` pointing to `https://github.com/epinter/LibreHardwareService` and `https://github.com/GameTechDev/PresentMon`.
  - `ModernWigiDash-v0.9.6-win-x64.zip`: `342,802,200` bytes (`326.9 MB`), including single-file `ModernWigiDash.App.exe` (`120,957,397` bytes).
  - `ModernWigiDash-v0.9.6-app-only.zip`: `114,202,927` bytes (`108.9 MB`, `3.00x`).
  - Sandbox verification: `Release package sandbox smoke verification PASSED.`

## Post-release checks

- Verify `gh release view v0.9.6 --json isDraft,isPrerelease,author,assets`: `isDraft=false`, `isPrerelease=false`, `author=github-actions[bot]`, and `releases/latest` resolves to `v0.9.6`.
- Verify both `ModernWigiDash-v0.9.6-win-x64.zip` and `ModernWigiDash-v0.9.6-app-only.zip` carry `sha256:` digests and exact byte sizes matching the CI run log's `verified <name>: N bytes` lines (note: CI `Compress-Archive` zip hashes differ from local builds by timestamp metadata).
- Confirm release notes carry generated PR/commit notes and the ElmorLabs credit (`--generate-notes` + workflow header), and confirm in the downloaded full zip:
  - `telemetry\third-party-licenses\telemetry-versions.txt` records both lines.
  - `README.txt` starts with `ModernWigiDash v0.9.6` and includes `== What's new in v0.9.6 ==` and `== Version ==`.

## End-to-end update (from the published v0.9.5 payload)

Preconditions:

- Close every running ModernWigiDash instance so the extracted `v0.9.5` does not exit on the single-instance mutex.
- Back up `profile.json`, `app_theme.json`, and `calendar-credentials.bin` from `%LOCALAPPDATA%\ModernWigiDash`; restore them after the leg.

Steps:

- Download and extract the published `v0.9.5` app-only zip outside the repo, launch it, and apply the `v0.9.6` update through the in-app updater (`Restart to apply`).
- Confirm relaunch with `FileVersion 0.9.6` and `app=0.9.6` in the log, then inspect `%LOCALAPPDATA%\ModernWigiDash\updates\update.log` across the single `v0.9.5 -> v0.9.6` restart and record **both counts** below:
  - Per-user install `updater start args:` count: `1` (`swap complete`: `1`)
  - Elevated install `swap complete` count: `1` (`updater start args:` count `2`, `install dir not writable; requesting elevation` count `1`, verified in `C:\ModernWigiDash-e2e-elev` with `ConsentPromptBehaviorAdmin=0`; companion `v0.9.6` `apply-update.cmd` `Program Files` test also `updater start args:` `2`, `swap complete:` `1`)

## Rollback

- **Gates or build fails:** no release exists. Delete the remote and local tag, fix, re-tag (`git push --delete origin v0.9.6`, `git tag -d v0.9.6`).
- **Assets fail to upload:** fix on `master`, create a throwaway branch at the tag, push it, and dispatch the release workflow from it (`gh workflow run release.yml --ref <branch> -f tag=v0.9.6`); keep `Release -Version` equal to the tag without the `v`.
- **A broken release is already published:** do **not** delete the release and do **not** move the `v0.9.6` tag; ship a forward fix release (`v0.9.7`).

## Post-release result

- Prior tag `v0.9.6` on `498696c` had failed `Release` workflow `43530874217` in `Verify Quality Gates` (`DisplayWireTests.AdoptAndAbandon_ConcurrentTransfers_DrainsDisplacedAndAbandonedBackendsSafely`, `21ms` CI ThreadPool scheduling race) with no GitHub Release created (`gh release view v0.9.6` -> `release not found`). Per `release-v0.9.5.md` rollback procedure, deleted unpublished tag `v0.9.6`, committed deterministic test fixes as `f01062f`, re-tagged `v0.9.6` (`8cb494cc91db6e3c44e8bcbf27ac453d81421f4d`), and pushed `master` + `v0.9.6`.
- GitHub Actions runs on `f01062f`:
  - `Release`: run `43535669992` (`success`, `9m41s`).
  - `CI`: run `43535669513` (`success`).
  - `CodeQL`: run `43535669565` (`success`).
- Published GitHub Release (`gh release view v0.9.6`): `isDraft=false`, `isPrerelease=false`, `author.login=github-actions[bot]`, and `releases/latest` resolves to `v0.9.6`.
- Release notes start with the `ElmorLabs` credit and include `**Full Changelog**: https://github.com/headpiece747/ModernWigiDash/compare/v0.9.5...v0.9.6`.
- Published assets match CI run `43535669992` `verified <name>: N bytes` log lines:
  - `ModernWigiDash-v0.9.6-win-x64.zip`: `343,453,360` bytes, `sha256:d568e4a57ecffd7a6e6e3f66827b090f94cc771d7ba3296af9997ee4ef83ab2b`.
  - `ModernWigiDash-v0.9.6-app-only.zip`: `114,731,185` bytes, `sha256:ef57bae207c5fba35ecbc0c73969bf4b1f82793d560c49f38473b5036eabe4f1`.
- Downloaded `ModernWigiDash-v0.9.6-win-x64.zip` verified matching SHA-256 (`d568e4a57ecffd7a6e6e3f66827b090f94cc771d7ba3296af9997ee4ef83ab2b`):
  - `telemetry\third-party-licenses\telemetry-versions.txt`: `LibreHardwareService=0.3.4`, `PresentMon=2.6.0`.
  - `telemetry\third-party-licenses\NOTICES.txt`: lists `LibreHardwareService v0.3.4` (`MPL-2.0`, `LHS-LICENSE.txt`) and `PresentMon Service v2.6.0` (`MIT`, `PresentMon-LICENSE.txt`).
  - `README.txt`: starts with `ModernWigiDash v0.9.6 — G.Skill WigiDash widget stack` and includes `== Version ==` and `== What's new in v0.9.6 ==`.
- Per-user end-to-end update from published `v0.9.5` (`ModernWigiDash-v0.9.5-app-only.zip` extracted outside the repo to `%TEMP%\wmd-v095-e2e\app\ModernWigiDash-win-x64\`, `FileVersion=0.9.5`):
  - Launched `v0.9.5` (`pid=4700`) at `01:04:37.610Z` (`[App] === Host === ... app=0.9.5`); clicked `UpdateButton` via UIA to download and stage `v0.9.6` (`updates\update.log` gained `0` lines after download/stage, proving single-launch suppression of the early download-time updater spawn).
  - Clicked `UpdateButton` -> confirmed `OK` on `"Update ready — restart to apply"` dialog; `v0.9.5` closed cleanly at `01:20:40.709Z` (`[SHELL] window closed (reason=update-restart, trayLive=True)`) and `01:20:40.720Z` (`[App] === Application exiting === reason=update-restart`).
  - `%LOCALAPPDATA%\ModernWigiDash\updates\update.log` logged `updater start args:` at `20:20:40.73` local and `swap complete` at `20:20:40.90` local (`updater start args:` count = `1`, `swap complete` count = `1`, zero `ERROR: app did not exit within 60s`).
  - Relaunched `v0.9.6` (`pid=14952`, `FileVersion=0.9.6`) at `01:20:41.446Z` (`[App] === Host === ... app=0.9.6`) and passed `wmd-verify.ps1 doctor` (`enabled`). Startup cleanup removed `downloads/`, `staged/`, and `apply-update-live.cmd`, leaving only `updates\update.log`.
  - State restore & cleanup: `profile.json` restored byte-identical (`12,274` bytes, `mtimeUtc=2026-11-13T00:37:46.6899832Z`, `sha256:ea1062c849b645d1143a54ad40a42c4c8bb1fa4d015ea67587e3a554ae7aca65`); `app_theme.json` and `calendar-credentials.bin` were absent before and after; scratch directories removed.
  - Elevated end-to-end verification (`ConsentPromptBehaviorAdmin=0`, unelevated launch -> `:elevate` re-exec -> elevated `v0.9.6` swap & relaunch):
    - Published `v0.9.5 -> v0.9.6` in `C:\Program Files\ModernWigiDash-e2e-elev\` (`pid=17736`, unelevated): reproduced `v0.9.5`'s pre-fix `:elevate` space-splitting bug (`%~1` split at `Program Files` -> `C:\Program`, looping `requesting elevation` `576` times with `swap complete` `0`; staged/temp dirs cleaned up). Note: `v0.9.5` users in `Program Files` running unelevated must either run `v0.9.5` elevated once (`writable` path, zero `:elevate`) or overwrite with `v0.9.6` zip.
    - Published `v0.9.5 -> v0.9.6` in ACL-locked `C:\ModernWigiDash-e2e-elev\` (`Users:RX`, `Administrators:F`, unelevated `pid=24236` at `01:51:55.220Z` `app=0.9.5`): `0` updater lines during download/stage; restarted `02:07:22.998Z`; `updates\update.log` logged `updater start args:` (`21:07:23.01` local), `install dir not writable; requesting elevation` (`21:07:23.11` local), re-exec `updater start args:` (`21:07:23.72` local), and `swap complete` (`21:07:24.17` local) — `updater start args:` count = `2`, `requesting elevation` = `1`, `swap complete` = `1`; relaunched elevated `v0.9.6` (`pid=16680`, `runner_isAdmin=True`, `fver=0.9.6`, `doctor=ok`, `02:07:24.646Z` `app=0.9.6`).
    - Shipped `v0.9.6` `apply-update.cmd` in `C:\Program Files\ModernWigiDash-e2e-v096\` (with spaces, unelevated -> `:elevate`, `WMD_UPDATE_LOG`): `updater start args:` count = `2` (forwarding quoted `"C:\Program Files\ModernWigiDash-e2e-v096"`, `elevated`, and `%LOG%`), `requesting elevation` = `1`, `swap complete` = `1`, relaunched elevated `v0.9.6` (`pid=19440`, `fver=0.9.6`, `doctor=ok`, `02:07:55.700Z` `app=0.9.6`).
    - All elevated test directories removed, `ModernWigiDash.App` stopped, `updates\` clean (`update.log` only), and `profile.json` preserved/restored byte-identical (`12,274` bytes, `sha256:ea1062c849b645d1143a54ad40a42c4c8bb1fa4d015ea67587e3a554ae7aca65`).
