# Release v0.9.7

Status: pre-release verified (CI gates, ps-hygiene, ref-check, live WigiDashService + AIDA64 probe, and release package smoke verified)

## Target

Release tag `v0.9.7`.

## Artifacts

- `ModernWigiDash-v0.9.7-win-x64.zip` (full release bundle: single-file `ModernWigiDash.App.exe`, `LICENSE-ModernWigiDash.txt`, `README.txt`, `Resources\Fonts\Geist-VariableFont_wght.ttf`, `Resources\Logo\logo.ico`, `setup-telemetry.bat`, `telemetry/` offline MSIs + `telemetry-versions.txt`, `NOTICES.txt` + license files)
- `ModernWigiDash-v0.9.7-app-only.zip` (slim in-app updater payload: single-file `ModernWigiDash.App.exe`, `LICENSE-ModernWigiDash.txt`, `README.txt`, `Resources\Fonts\Geist-VariableFont_wght.ttf`, `Resources\Logo\logo.ico`)

## What changed

- **HWiNFO sensor discovery, fault isolation & recovery (`ModernWigiDash.Hardware`, `ModernWigiDash.Widgets`, `ModernWigiDash.App`, `ModernWigiDash.Sdk`)**:
  - `ISensorValues.cs` & `WigiDashServiceClient.cs`: Added `VendorSensorStatus` (`Disconnected`, `ProviderNotInitialized`, `EmptySensorList`, `Ready`) and split WCF channel management into isolated `_sensorSlot` (HWiNFO) and `_aidaSlot` (AIDA64) slots with `_connectLock` serialization outside `_gate`. Separated `_lastConnectAttemptUtc` and `_lastReinitAttemptUtc` 5 s retry clocks so initial connection never blocks immediate empty-list re-initialization. Isolated per-sensor SOAP `FaultException` (`faultedKeys` eviction only) from transport faults (`failAll`), configured `UseDefaultWebProxy = false` with 20 MB buffers and explicit `ReaderQuotas`, and added `SetPollingPeriod(TimeSpan)` scaling `_sensorValueTtl` (`150–2000 ms`).
  - `VendorSensorRefresh.cs` & `HwinfoWidget.cs`: Added `VendorRefreshFailure`, invalidated cached sensor lists only when `Status != VendorSensorStatus.Ready` (preventing list thrashing on transient `null` reads), bounded `Automatic` probing (`<= 16` candidates) with `_resolvedAutoKey` latching and automatic unlatching/re-probing if the latched key returns `null` or `!IsValid`, and mapped status-specific placeholder subtitles (`"Start G.SKILL WigiDashService"`, `"WigiDashService HWiNFO provider not ready"`, `"No HWiNFO sensors reported"`, `"Saved sensor unavailable — re-pick in Inspector"`).
  - `Attributes.cs` & `InspectorPanelRenderer.cs`: Added `WidgetPropertyOption.IsUnavailable` and wired `BuildChoiceEditor` to show `"The saved option is unavailable. Select an available option."` when a persisted key is missing from live choices, collapsing the warning live on `ComboBox.SelectionChanged`.
- **Settings-driven telemetry & AIDA64 polling synchronization (`ModernWigiDash.Sdk`, `ModernWigiDash.Hardware\Aida64`, `ModernWigiDash.App`, `ModernWigiDash.Widgets`)**:
  - `PollLoop.cs` & `TelemetryFeedCoordinator.cs`: Added `PollLoop.ValidateInterval` and non-blocking `PollLoop.SetInterval(TimeSpan)` mutating `PeriodicTimer.Period` in place under `_timerGate`. `TelemetryFeedCoordinator` propagates `TelemetryPollPeriodMs` (`250` / `500` / `1000` / `2000 ms`) at startup, `SetVendorService`, and `UpdatePollingPeriod` to hardware poll loops, `WigiDashServiceClient`, and `AidaPanelService`.
  - `AidaPanelService.cs`, `AidaPanelMaster.cs`, `IAidaMmapSource.cs`, & `MemoryMappedAidaMmapSource.cs`: Added `AidaPanelService.ResolvePollInterval` (`Clamp(T / 2, 150..500 ms)` Nyquist sampling) and `SetPollingPeriod` / `UpdatePollingPeriod`. Serialized `MemoryMappedAidaMmapSource` `TryRead`, `Reset`, and `Dispose` under `_mutexGate`, separating permanent ACL-denial latching (`_mutexDenied`) from a 5 s retry cooldown for transient mutex absence. Updated `AidaPanelMaster` with 10-tick header reset checks, 5 s producer-inactivity frame clearing, 1-tick torn-read deferral, and separation of MMF read errors from WCF write failure strikes.
  - `AidaPanelCanvas.cs`, `AidaPanelWidget.cs`, & `SettingsDialog.cs`: Added status-aware AIDA64 placeholder subtitles (`GetUnavailableStatus()`) and updated the Settings `Telemetry Polling Rate` hint.
  - `RetiredBitmapSet.cs` & `RetiredBitmapSetTests.cs`: Fixed `Drain()` to snapshot `_pending.ToArray()` before `_pending.Clear()`, eliminating both the concurrent `InvalidOperationException` when `Retire` races `DisposeRetired` and the skipped disposal of drained bitmaps.
  - Updated `CONTEXT.md`, `docs\adr\0025-vendor-reads-from-a-background-cache.md`, `docs\adr\0028-configurable-telemetry-polling-rate-and-nyquist-cadence.md`, and `release\README.txt`.

## Procedure

Owner: `.github/workflows/release.yml` on annotated tag push (no release created, edited, or deleted by hand).

1. Run `powershell -NoProfile -File scripts\ps-hygiene.ps1`, `powershell -NoProfile -File scripts\ref-check.ps1`, and `powershell -NoProfile -File scripts\run-gates.ps1 -Label "v0.9.7 release-ready"` within the 60-minute gate freshness window (`scripts\gate-guard.ps1`).
2. Verify `scripts\build-release.ps1 -Version 0.9.7` locally (using `scripts\.release-cache`) and confirm sandbox smoke verification passes, then remove local root `.zip` artifacts before committing.
3. Commit release-ready changes, create annotated tag `v0.9.7` (`git rev-parse HEAD` == `git rev-list -n1 v0.9.7`), push `master` and tag `v0.9.7` so `.github/workflows/release.yml` builds and publishes both zips to GitHub Releases.

## Pre-flight evidence

- `scripts\ps-hygiene.ps1`: `17` `.ps1` files pure ASCII, analyzer clean, `33/33` Pester tests passed (`0` failed, `0` skipped).
- `scripts\ref-check.ps1`: `5` docs checked, `0` stale references.
- `scripts\run-gates.ps1 -Label "v0.9.7 release-ready"`: `0` build warnings, `0` errors, `3098/3098` tests passed (`0` failed), `dotnet format` clean.
- `scripts\build-release.ps1 -Version 0.9.7` (using version-keyed cache under `scripts\.release-cache`):
  - Telemetry bundled (`telemetry-versions.txt`): `LibreHardwareService=0.3.4`, `PresentMon=2.6.0`, with `LHS-LICENSE.txt`, `PresentMon-LICENSE.txt`, and `NOTICES.txt`.
  - `ModernWigiDash-v0.9.7-win-x64.zip`: `342,810,559` bytes (`326.9 MB`), including single-file `ModernWigiDash.App.exe` (`120,965,968` bytes).
  - `ModernWigiDash-v0.9.7-app-only.zip`: `114,211,286` bytes (`108.9 MB`, `3.00x`).
  - Sandbox verification: `Release package sandbox smoke verification PASSED.`
- Live host verification against `WigiDashService` (`PID 5936`, integrated `HWiNFO64.dll`) and `AIDA64 Extreme v7.70` (`aida64.exe`, `HWMonEnableGSLCD=1`, `5x4 (1016x592 px)`):
  - `WigiDashServiceClient` + `HwinfoWidget`: `Status=Ready`, `SensorCount=685`, `686` dropdown options (`Automatic` + `685` live sensors), live sensor reading verified (`20473 MB`, `Valid=True`).
  - `AidaPanelWidget` + `AidaPanelService`: received live `1016x592` (`1202944` bytes) RGB565 frames (`gen=1..6`, `lastErr=<none>`) across live `SetPollingPeriod` transitions (`250 -> 150 ms`, `1000 -> 500 ms`, `2000 -> 500 ms`, `500 -> 250 ms`).
