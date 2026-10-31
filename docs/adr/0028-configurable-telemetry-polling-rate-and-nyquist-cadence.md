# ADR-0028: Configurable telemetry polling rate and Nyquist sampling cadence

**Date:** 2026-10-31
**Status:** Accepted
**Deciders:** Project owner

## Context

A user reported that sensor readouts were updating slower than 1 second, observing freezes near 2 seconds between updates. ModernWigiDash was querying telemetry every 1.000s in `TelemetryFeedCoordinator`, while LibreHardwareService publishes hardware sensor updates every ~1.000s plus sensor I/O latency (~50ms).

Because consumer and producer ran at nearly identical unsynchronized frequencies, phase drift caused consumer sampling ticks to periodically fall within the same sensor update window. This produced consecutive ticks with unchanged data, creating an observable ~2.0-second UI freeze (the Nyquist-Shannon sampling violation).

Auditing official G.Skill WigiDash software (`WigiDashManager.exe`, `WigiDashServiceLibrary.dll`, `28DDDE13-824F-4C45-A8F0-A61CFAB123E6.dll`) revealed:
1. G.Skill defaults its sensor update cadence to **500 ms** (`HardwareSensors.UpdateInterval = 500`), exactly 2x the base sensor write cadence.
2. G.Skill exposes a user setting for HWiNFO polling interval (`HwinfoUpdateInterval`, saved to registry `HKCU\Software\G.Skill\WigiDash Manager\HWInfo_UpdateInterval`).
3. G.Skill's `SensorTask` reads sensor values for monitored items without re-fetching the full 600+ item sensor list on every cycle.

In ModernWigiDash, `WigiDashServiceClient` had `RefreshMinInterval = 1000ms`, capping background updates to at most once per second regardless of widget settings. It also re-fetched the full sensor list on every refresh pass over SOAP WCF. Additionally, `VendorSensorRefresh.ReadingRefreshMs` was throttled at 500ms.

## Decision

1. **Default Cadence Changed to 500 ms:**
   `TelemetryFeedCoordinator` defaults sensor and frame-time polling to **500 ms**, satisfying the Nyquist criterion (2 Hz sampling against a 1 Hz source) and matching official G.Skill behavior. This eliminates the phase-beat stall out-of-the-box.

2. **Machine-Local Configuration:**
   Added `TelemetryPollPeriodMs` to `AppSettings` (persisted in `%LOCALAPPDATA%\ModernWigiDash\app_settings.json`), with normalized options:
   - `250 ms (Fast)`
   - `500 ms (Normal) [Default]`
   - `1000 ms (Relaxed)`
   - `2000 ms (Eco)`
   Machine-local storage ensures imported profiles cannot overwrite a user's chosen polling rate.

3. **User Setting in Settings Hub:**
   Exposed a "Telemetry Polling Rate" ComboBox in `SettingsDialog` under the Behavior group.

4. **Dynamic Live Update:**
   `TelemetryFeedCoordinator.UpdatePollingPeriod` safely disposes running poll loops and instantiates new ones at the updated rate. Changes apply immediately without requiring an application restart. `MainWindow` pre-loads `_appSettings` before telemetry wiring to ensure boot-time rates match persisted values.

5. **WCF Service Client & Throttle Optimization:**
   `WigiDashServiceClient` reduces `RefreshMinInterval` and `SensorValueTtl` to 250ms. `RefreshCache()` caches `GetRawSensorList()` and only fetches values when cached, eliminating redundant 600+ item SOAP calls. `VendorSensorRefresh` throttle is reduced to 250ms.

## Consequences

- The 2-second phase-beat stall is permanently resolved for all users on default settings.
- Users can choose high responsiveness (250ms) or low overhead (2000ms).
- WCF traffic between ModernWigiDash and the vendor service is drastically reduced.
- All changes are verified on physical WigiDash hardware (`VID=0x28DA, PID=0xEF01`) and covered by unit tests.
