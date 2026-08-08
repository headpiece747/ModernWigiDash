# ADR-0004: LibreHardwareService Shared Memory as Hardware Sensor Source

**Date:** 2026-08-08
**Status:** Accepted
**Deciders:** Project owner

## Context

The hardware-sensor path (`LhmSensorReader` → WCF `GetSensorSnapshot` → `LhmSensorStore` → `HardwareMonitorWidget`) captures readings via LibreHardwareMonitorLib from inside the project's own `ModernWigiDashService` (LocalSystem). That design has two problems, mirroring the frame-time situation ADR-0003 already resolved:

1. **Elevation surface**: every sensor widget inherits the "own service must be running" coupling, and the service's LHM reader is custom code that must stay correct against LibreHardwareMonitor's hardware-update contract.
2. **Dupes a solved problem**: LibreHardwareService (the reference client for the LHM fork) already runs as a LocalSystem service, owns the hardware polling, and publishes readings to **named shared-memory maps** (`sensors`, `all-hardware`, `status`) guarded by named mutexes — readable by any non-elevated process.

Separately, the project's own `ModernWigiDashService` is **isolated** (ADR-0003): kept in the repo but not used at runtime. Its three historical roles were USB transport (direct-USB engine replaced it), LHM sensor capture (this ADR), and frame-time ETW capture (already externalized to PresentMon). With sensors externalized, the service's sensor role is gone entirely.

Goal for this change (grilled with the owner): sensors work with **zero elevation** in the app process, with **no re-enabled own service**, and with **lower CPU/memory overhead** than the WCF + LHM-in-service design.

## Decision

**Replace the in-house WCF sensor capture with LibreHardwareService's shared memory** as the sole hardware-sensor source.

- **Read maps by name**: the app opens the named maps (`sensors`, `status`; `all-hardware` optional), taking the named mutex before each read, and parses the documented header (`MetaDataSize`, `UpdateInterval`, `LastUpdate`, `index-length/offset`, `index-format`, `data-length/offset`, reserved).
- **Support both index formats**: the header's `index-format` field is honored — parse the index as **JSON or MessagePack** (LHS default is MessagePack; the data blocks are JSON either way). Adds the **MessagePack-CSharp** package (not referenced anywhere in the solution today).
- **Own managed C# reader**: a pure C# shared-memory reader in the App (`LhmSharedMemoryReader`) — no native DLL, no P/Invoke of the C++ `lhwservice` client lib, fully testable like `PresentMonBlobReader`.
- **Producer swap, not rewrite**: the reader runs on the existing 1s `PollLoop` shape, maps LHS `DataSensor` records into `SensorSnapshotDto`, and writes through the existing `LhmSensorStore.UpdateFromDto` seam. The store, `HardwareMonitorWidget`, and their tests survive unchanged. `SensorId` is preserved (LHS publishes the same LHM identifier, e.g. `/amdcpu/0/temperature/0`) — the widget's stable machine key is untouched.
- **`Avg` dropped**: LHS publishes `value/min/max` only, not `avg`. The DTO's `Avg` is written as `0`; the widget already falls back to `Max` for its auto-scale reference (`Math.Max(reading.Max, reading.Avg)`). No time-window history is enabled on the service (that would raise its CPU/memory and defeat goal (d)).
- **`Unit` mapping replicated**: `UnitFor(SensorType)` currently lives as a private static in the Service's `LhmSensorReader`; the app-side producer carries its own copy.
- **Graceful fallback**: when LibreHardwareService is absent, the widget shows a "LibreHardwareService not running" empty state (the same pattern as PresentMon) — no crash, no admin prompt.
- **Full removal of the old WCF sensor path**: delete `LhmSensorReader` **and** the `GetSensorSnapshot` operation everywhere — contract (`IModernWigiDashDisplayServiceContract`), service implementation (`ModernWigiDashDisplayService`), client wrapper (`ModernWigiDashDisplayServiceClient`), the App's old WCF sensor poll producer (`MainWindow.ServiceIntegration`), and the tests covering them (`ServiceHostSmokeTests`, `WcfDisplayServiceTests`, `TelemetryStoreMappingTests` where reader-specific). `SensorSnapshotDto`/`SensorReadingDto` **stay** — they are the mailbox format the new producer reuses.
- **`ModernWigiDashService`**: still isolated, not used at runtime, code preserved except the sensor path removed by this ADR.

## Consequences

**Positive:**
- Removes per-run elevation for every hardware-sensor widget — one elevated install registers LibreHardwareService; the app then works non-elevated forever.
- Deletes the in-house LHM host (hardware-update contract, background worker, WCF surface) in favor of a maintained service.
- Lower steady-state CPU/memory than WCF + LHM-in-service: no pipe hop, no own worker, no LHM lib loaded in the app.
- The widget, `LhmSensorStore`, `PollLoop`, and DTO shapes survive unchanged — the blast radius is the producer and the old WCF op.

**Negative:**
- New runtime dependency on LibreHardwareService (installed with admin once; app detects it at runtime). LHS is a third-party service with its own release cadence — the app must tolerate its absence (fallback) and re-verify the map format on upgrades.
- New NuGet dependency **MessagePack-CSharp** for the default index format.
- `avg` is no longer displayed — the widget's gauge reference degrades from `max/max(avg)` to `max` alone.

**Rationale:** Hardware reading is exactly LibreHardwareService's domain, and its documented architecture (LocalSystem service + mutex-guarded named shared-memory maps + non-elevated client) matches the project's isolation and non-elevation goals with no custom hardware-polling code to maintain — the same reasoning that drove ADR-0003.

## Alternatives considered

1. **Keep the in-house WCF + LHM reader** — retained the own-service coupling and the custom LHM maintenance burden. Rejected: duplicates LibreHardwareService's solved problem.
2. **P/Invoke the C++ `lhwservice` client library** — native DLL, CMake/vcpkg build, C ABI marshaling, distribution weight. Rejected: a pure C# reader is smaller and testable.
3. **JSON index only (`indexFormat: 1`)** — no new package, but the app then *requires* LibreHardwareService configured that way; fragile against a manual/stock install. Rejected: support both formats via the header field.
4. **Enable LHS time-window + compute avg client-side** — restores avg but raises service CPU/memory and per-sensor app state. Rejected against goal (d).

## Date

2026-08-08
