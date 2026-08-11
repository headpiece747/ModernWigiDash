# LibreHardwareService Shared Memory as Hardware Sensor Source — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the in-house WCF + LibreHardwareMonitorLib sensor path (`LhmSensorReader` → WCF `GetSensorSnapshot` → `LhmSensorStore`) with a direct App-side reader over LibreHardwareService's named shared-memory maps (ADR-0004). Sensors work with zero elevation, no own service, lower steady-state CPU/memory. Old WCF sensor path removed entirely.

**Architecture:** Pure producer swap at the App boundary. New `LhmSharedMemoryReader` in `ModernWigiDash.App` reads LHS's `sensors` map (mutex-guarded, non-persisted, Everyone-read ACL), maps `DataSensor` records into `SensorSnapshotDto`, and feeds the existing `LhmSensorStore.UpdateFromDto` seam — store, `HardwareMonitorWidget`, `PollLoop`, and DTO shapes survive unchanged. The WCF `GetSensorSnapshot` operation, `LhmSensorReader`, and the `LibreHardwareMonitorLib` package are deleted.

**Tech Stack:** .NET 10, C# 14, `System.IO.MemoryMappedFiles` + `System.Threading.Mutex` + `System.Text.Json` (data blocks are always JSON), MessagePack-CSharp (index format 2, the LHS default). MSTest for the new reader tests.

## Global Constraints

- `SensorSnapshotDto` / `SensorReadingDto` STAY (Service.Contracts) — they are the mailbox format the new producer reuses; `LhmSensorStore`, `HardwareMonitorWidget`, and their tests are untouched.
- `Avg` is dropped: LHS publishes `value/min/max` only → DTO `Avg = 0` (widget already falls back to `Max` in `SystemTelemetryWidgets.cs:105`).
- `UnitFor(SensorType)` is replicated app-side from the deleted Service reader (`LhmSensorReader.cs:161-185`) — same unit table, string-keyed on the LHM `SensorType.ToString()` value LHS publishes.
- `SensorId` preserved: LHS publishes the same LHM identifier (`/amdcpu/0/temperature/0`), so the widget's stable machine key is untouched.
- Graceful fallback: when LHS is absent, `Poll()` returns `IsConnected=false` — the widget renders "LibreHardwareService not running" (same pattern as PresentMon).
- Reader is testable through a pure parser seam (`TryParse(byte[])`) — no map opening in tests (hardware boundary, mirror `PresentMonFrameTimeProducerTests` faking the native seam).
- Verify with the temp-output test command when the service is running: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`.

## LHS Wire Format (verified against epinter/LibreHardwareService `main`)

Map names (non-persisted, `Global\` namespace, Everyone-read ACL):
- Sensors: `Global\LibreHardwareService/json/sensors/data` — mutex `Global\LibreHardwareService/json/sensors/data/MUTEX`
- Status: `Global\LibreHardwareService/json/status/data` (not needed by this change)
- All-hardware: `Global\LibreHardwareService/json/all/data` (feature-flag off by default; not needed)

`writeSensors` header layout (offsets in bytes, all little-endian):
| Offset | Type | Field |
|--------|------|-------|
| 0 | int | `MetaDataSize` (= 4 + 8 = 12) |
| 4 | int | `UpdateInterval` (ms) |
| 8 | long | `LastUpdate` (Unix seconds) |
| `msb = 4 + MetaDataSize` (=16) | int | index-length |
| `msb + 4` | int | index-offset |
| `msb + 8` | int | index-format (1=JSON, 2=MessagePack) |
| `msb + 12` | int | data-length |
| `msb + 16` | int | data-offset |
| `msb + 20` | int[4] | reserved |

- `indexOffset = msb + 4 + ((msb + 20) * 4)` (= 164). `dataOffset = indexOffset + indexLen + 4` (4-byte zero padding).
- Index: serialized `List<DataIndex>` where each entry = `{identifier, offset, size, sensorName, sensorType, hardwareName}`. **`offset` is relative to the start of the data blob** (verified: `SensorsManager` sets `offset = stream.Position` of the data stream — the comment in `MemoryMappedSensors.cs` is misleading).
- Data: concatenated Utf8Json-serialized `DataSensor` objects, each followed by a single `0x00` byte. **Always JSON regardless of index-format.** `DataSensor` JSON fields: `identifier, name, sensorType, hardwareId, hardwareName, hardwareType, value, max, min, valuesTimeWindow, values[]`.
- Read protocol: open mutex → `WaitOne` (short timeout) → open map read-only → read header → read index bytes at `indexOffset`/`indexLen` → read data bytes at `dataOffset`/`dataLen` → release mutex. Parse index per `index-format`, slice `data[offset .. offset+size]` per entry, JSON-parse each `DataSensor`.

---

### Task 1: Add MessagePack package (CPM + App + Tests)

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `ModernWigiDash.App/ModernWigiDash.App.csproj`
- Modify: `ModernWigiDash.Tests/ModernWigiDash.Tests.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `MessagePack` version pinned centrally (CPM); `PackageReference` in App (reader) and Tests (index fixture serialization).

- [ ] **Step 1: Resolve the latest stable MessagePack version**

Run: `dotnet package search MessagePack --take 3`
Expected: package ID `MessagePack` with the current stable 2.x/3.x version. Do NOT hardcode a version from memory.

- [ ] **Step 2: Pin centrally in `Directory.Packages.props`**

Add under the existing `<ItemGroup>` (alphabetical, between `LibreHardwareMonitorLib` and `LibUsbDotNet` if kept, else near `MSTest.*`):

```xml
    <PackageVersion Include="MessagePack" Version="<resolved>" />
```

- [ ] **Step 3: Reference in App and Tests**

- `ModernWigiDash.App/ModernWigiDash.App.csproj` — add to the existing `PackageReference` `ItemGroup`: `<PackageReference Include="MessagePack" />`
- `ModernWigiDash.Tests/ModernWigiDash.Tests.csproj` — add `<PackageReference Include="MessagePack" />` (needed by the MessagePack-index fixture test in Task 5).

- [ ] **Step 4: Build to verify the package resolves**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props ModernWigiDash.App/ModernWigiDash.App.csproj ModernWigiDash.Tests/ModernWigiDash.Tests.csproj
git commit -m "deps(sensors): add MessagePack-CSharp for LHS index parsing (ADR-0004)"
```

---

### Task 2: LhmSharedMemoryReader (App) — TDD, pure parser first

**Files:**
- Create: `ModernWigiDash.App/LibreHardwareService/LhmSharedMemoryReader.cs`
- Create: `ModernWigiDash.Tests/LhmSharedMemoryReaderTests.cs`

**Interfaces:**
- Consumes: `SensorSnapshotDto`, `SensorReadingDto` (Service.Contracts). No store coupling — returns the DTO.
- Produces: `LhmSharedMemoryReader` (public sealed, no IDisposable — map/mutex opened per `Poll()`), with:
  - `public SensorSnapshotDto Poll()` — one full read of the LHS sensors map.
  - `internal static SensorSnapshotDto TryParse(byte[] mapBytes)` — pure parser seam (header → index → data → DTO), the unit-test surface.
  - `internal static string UnitFor(string sensorType)` — replicated unit table.

**Protocol constants (internal):**
- `SensorsMapName = @"Global\LibreHardwareService/json/sensors/data"`, `SensorsMutexName = @"Global\LibreHardwareService/json/sensors/data/MUTEX"`.
- Header offsets: `OffsetMetaDataSize=0`, `OffsetUpdateInterval=4`, `OffsetLastUpdate=8`, `IndexLength=16`, `IndexOffset=20`, `IndexFormat=24`, `DataLength=28`, `DataOffset=32`, `Reserved=36`.
- `IndexFormatJson=1`, `IndexFormatMessagePack=2`. `MetadataBlockSize = 4 + MetaDataSize` (verified 16).

**Step order (red-green-refactor per the tdd skill):**

- [ ] **Step 1: Red — write `LhmSharedMemoryReaderTests` for the parser seam**

Cover (each its own `[TestMethod]`, AAA):
1. `TryParse_JsonIndexWithTwoSensors_MapsAllFields` — build a synthetic map byte array: header ints, JSON index, data blob of two Utf8Json `DataSensor` objects each null-terminated; assert `IsConnected`, `LastUpdate`, `Readings.Count`, and per-reading `SensorId`/`SensorName`/`HardwareName`/`HardwareType`/`SensorType`/`Unit`/`Value`/`Min`/`Max`, and `Avg == 0`.
2. `TryParse_MessagePackIndex_MapsSensors` — same fixture but index serialized with `MessagePackSerializer.Serialize(List<IndexEntry>)` and `index-format = 2`; assert equal mapping (proves the MessagePack dependency is exercised).
3. `TryParse_SensorIdPreserved` — `SensorId` equals the LHS `identifier` verbatim (machine-key stability).
4. `TryParse_TruncatedHeader_ReturnsDisconnected` — mapBytes too short to contain the header → `IsConnected=false`, `Readings` empty, no throw.
5. `TryParse_TruncatedData_ReturnsDisconnected` — header valid but index/data offsets beyond `mapBytes.Length` → disconnected, no throw.
6. `TryParse_EmptyMap_ReturnsDisconnected`.
7. `UnitFor_SensorTypeStrings_ReturnsExpectedUnits` — table-driven over the LHS `SensorType.ToString()` values (Temperature→°C, Fan→RPM, Voltage→V, Clock→MHz, Load→%, Power→W, Current→A, Throughput→MB/s, Frequency→Hz, Control→%, Level→%, Data→GB, SmallData→MB, Flow→L/h, Factor→"", TimeSpan→s, Timing→ns, Energy→mWh, Noise→dBA, Conductivity→µS/cm, Humidity→%, unknown→"").

Test helper to build the synthetic map must mirror the writer exactly (`metadataBlockSize`/`indexOffset`/`dataOffset` math from the table above) so the parser and the fixture agree by construction.

Run tests: `dotnet test ModernWigiDash.Tests -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter FullyQualifiedName~LhmSharedMemoryReaderTests`
Expected: compile error (type missing) — red.

- [ ] **Step 2: Green — implement `LhmSharedMemoryReader.TryParse` + models**

In `LhmSharedMemoryReader.cs`:
- `internal sealed class IndexEntry { [Key(0)] string Identifier; [Key(1)] int Offset; [Key(2)] int Size; [Key(3)] string SensorName; [Key(4)] string SensorType; [Key(5)] string HardwareName; }` — `[MessagePackObject]` + `[Key]` for the MessagePack path; JSON path via `System.Text.Json` case-insensitive (`PropertyNameCaseInsensitive = true`) over the same properties.
- `internal sealed record SensorBlock` (identifier, name, sensorType, hardwareId, hardwareName, hardwareType, value, min, max) — System.Text.Json camelCase fields.
- `TryParse(byte[] mapBytes)`:
  - Guard `mapBytes.Length >= Reserved + 16` else disconnected.
  - Read `MetaDataSize`, `UpdateInterval`, `LastUpdate` (long, Unix seconds).
  - `msb = 4 + MetaDataSize`; read index/data lengths+offsets+format.
  - Guard all offsets/lengths in bounds → else disconnected.
  - Parse index (JSON or MessagePack per `index-format`), then for each entry slice `data[offset..offset+size]` → JSON deserialize `SensorBlock`.
  - Map to `SensorReadingDto` (`Avg = 0`, `Unit = UnitFor(sensorType)`).
  - `LastUpdate = DateTimeOffset.FromUnixTimeSeconds(LastUpdateLong).UtcDateTime`; if `LastUpdateLong <= 0`, disconnected.
  - Any parse exception → disconnected snapshot (never throw).

- [ ] **Step 3: Green — implement `Poll()`**

- `Poll()`:
  1. `using var mutex = Mutex.OpenExisting(SensorsMutexName);` — try/finally. `if (!mutex.WaitOne(TimeSpan.FromMilliseconds(100)))` → disconnected (writer holds it; avoid torn read). Catch `AbandonedMutexException` → proceed (only the service writes). `WaitHandle.WaitTimeout` → disconnected.
  2. `using var map = MemoryMappedFile.OpenExisting(SensorsMapName, MemoryMappedFileAccess.Read);` → `using var accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);` → copy the map to a byte array (read header first to size the copy, or copy a bounded prefix — read the header, compute `dataOffset + dataLen`, copy that many bytes into a managed buffer, then `TryParse`).
  3. Release mutex in `finally` (ignore `ApplicationException` if the mutex was abandoned).
- Any exception (map absent, access denied, parse failure) → `new SensorSnapshotDto { IsConnected = false, LastUpdate = now, Readings = [] }`. Log once per change (mirror `FrameTimePollTick`'s `_lastFrameTimeError` dedupe pattern — the reader can expose a `LastError` string the poll tick logs).
- `UnitFor(string sensorType)` — copy the deleted Service table (`LhmSensorReader.cs:161-185`), string-keyed.

Run the Task 2 tests again: all green.

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.App/LibreHardwareService/LhmSharedMemoryReader.cs ModernWigiDash.Tests/LhmSharedMemoryReaderTests.cs
git commit -m "feat(sensors): add LhmSharedMemoryReader over LibreHardwareService maps (ADR-0004)"
```

---

### Task 3: Rewire MainWindow sensor poll (direct LHS, start immediately)

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs`
- Modify: `ModernWigiDash.App/MainWindow.ServiceIntegration.cs`

**Interfaces:**
- Consumes: `LhmSharedMemoryReader` (Task 2), existing `_sensorPoll`, `LhmSensorStore.UpdateFromDto`.
- Produces: sensor poll decoupled from WCF routing; started immediately like the frame-time poll.

- [ ] **Step 1: Add the reader field + construction**

`MainWindow.xaml.cs`, field region (near `_presentMonProducer`, ~line 69-73):

```csharp
    // LibreHardwareService sensor producer (ADR-0004) — reads the named
    // shared-memory maps directly, independent of the WCF routing state.
    private readonly LhmSharedMemoryReader _lhsReader = new();
```

Namespace: add `using ModernWigiDash.App.LibreHardwareService;` (check the file's existing usings).

- [ ] **Step 2: Change the `_sensorPoll` ready gate to always-ready and start it immediately**

`MainWindow.xaml.cs:124-125` — the gate is no longer `ServiceReady` (LHS is direct, self-reporting):

```csharp
        _sensorPoll = new Sdk.PollLoop(
            "SENSOR", TimeSpan.FromSeconds(1), () => true, SensorPollTick, () => { }, msg => Log(msg));
```

Add `_sensorPoll.Start();` next to `_frameTimePoll.Start();` (`MainWindow.xaml.cs:135`). Update the comment at lines 64-65 ("WCF poll loops — one parameterized loop module per producer (touch, sensor). Constructed in the ctor, started on connect.") to reflect that only TOUCH is WCF-gated; SENSOR and FRAMETIME are direct producers started immediately.

- [ ] **Step 3: Rewrite `SensorPollTick` to call the reader**

`MainWindow.ServiceIntegration.cs:113-121`:

```csharp
    /// <summary>
    /// One LHS sensor probe (ADR-0004): reads the LibreHardwareService
    /// shared-memory map and caches the snapshot in <see cref="LhmSensorStore"/>
    /// so widgets read it without a WCF round-trip.
    /// </summary>
    private void SensorPollTick()
    {
        var dto = _lhsReader.Poll();
        LhmSensorStore.UpdateFromDto(dto);
    }
```

- [ ] **Step 4: Remove the WCF-gated start**

`MainWindow.ServiceIntegration.cs:76` — delete `_sensorPoll.Start();` from `InitializeWcfRoutingAsync` (only `_touchPoll.Start();` remains at line 75).

- [ ] **Step 5: Keep the stop in the Closed handler**

`MainWindow.xaml.cs:197` — `_sensorPoll.Stop();` stays (no change). `_lhsReader` needs no Dispose (map/mutex opened per poll).

- [ ] **Step 6: Build**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs ModernWigiDash.App/MainWindow.ServiceIntegration.cs
git commit -m "feat(sensors): poll LibreHardwareService shared memory directly, not via WCF (ADR-0004)"
```

---

### Task 4: Remove the WCF sensor path + LhmSensorReader + LibreHardwareMonitorLib

**Files:**
- Modify: `ModernWigiDash.Service.Contracts/IModernWigiDashDisplayServiceContract.cs`
- Modify: `ModernWigiDash.Service/Wcf/ModernWigiDashDisplayService.cs`
- Modify: `ModernWigiDash.Service.Contracts/ModernWigiDashDisplayServiceClient.cs`
- Modify: `ModernWigiDash.Service/Program.cs`
- Modify: `ModernWigiDash.Tests/ServiceHostSmokeTests.cs`
- Modify: `ModernWigiDash.Tests/WcfDisplayServiceTests.cs`
- Modify: `ModernWigiDash.Service/ModernWigiDash.Service.csproj`
- Modify: `Directory.Packages.props`
- Delete: `ModernWigiDash.Service/Services/LhmSensorReader.cs`

**Interfaces:**
- Consumes: the `SensorSnapshotDto`/`SensorReadingDto` contracts (which stay).
- Produces: no `GetSensorSnapshot` anywhere; no `LhmSensorReader` type; no `LibreHardwareMonitorLib` package.

- [ ] **Step 1: Remove `GetSensorSnapshot` from the contract**

`IModernWigiDashDisplayServiceContract.cs:91-99` — delete the XML doc + `[OperationContract]` pair + `SensorSnapshotDto GetSensorSnapshot();`.

- [ ] **Step 2: Remove the service implementation surface**

`ModernWigiDashDisplayService.cs`:
- Line 26: delete `private readonly LhmSensorReader? _lhmSensorReader;`
- Lines 44-51: drop `LhmSensorReader? lhmSensorReader = null,` from the ctor signature (and its `,` after `ServiceCallState callState` — keep `TimeProvider? timeProvider = null` last).
- Line 56: delete `_lhmSensorReader = lhmSensorReader;`
- Lines 315-331: delete the entire `GetSensorSnapshot()` method.
- Verify the `using ModernWigiDash.Service.Services;` (line 5) — if no other `Services` type is referenced, remove it; otherwise keep (check `DisplayHardwareWorkerService` usage — it is referenced from `Program.cs`, so the namespace import there stays; `ModernWigiDashDisplayService.cs` only used `LhmSensorReader` from it).

- [ ] **Step 3: Remove the client wrapper**

`ModernWigiDashDisplayServiceClient.cs:190-194` — delete `GetSensorSnapshot()`.

- [ ] **Step 4: Remove the DI registrations**

`Program.cs:316-319` — delete the `LhmSensorReader` comment + `AddSingleton<LhmSensorReader>()` + `AddSingleton<IHostedService>(...)`.

- [ ] **Step 5: Delete `LhmSensorReader.cs`**

`git rm ModernWigiDash.Service/Services/LhmSensorReader.cs` (the file holds `UnitFor` — already replicated in Task 2).

- [ ] **Step 6: Drop the package from Service + central props**

- `ModernWigiDash.Service/ModernWigiDash.Service.csproj:31` — delete `<PackageReference Include="LibreHardwareMonitorLib" />`.
- `Directory.Packages.props:10` — delete `<PackageVersion Include="LibreHardwareMonitorLib" Version="0.9.6" />`.

- [ ] **Step 7: Update tests**

- `ServiceHostSmokeTests.cs:23` — delete `Assert.IsNotNull(app.Services.GetRequiredService<LhmSensorReader>());`. Line 37 — delete `Assert.IsTrue(hosted.OfType<LhmSensorReader>().Count() == 1);`. Check the `using ModernWigiDash.Service.Services;` (line 6) — remove if now unused.
- `WcfDisplayServiceTests.cs:136-144` — delete `GetSensorSnapshot_WithoutReader_ReturnsDisconnectedSnapshot` (its `SensorSnapshotDto`/`Service.Contracts` references may still be used elsewhere in the file — verify before touching usings).

- [ ] **Step 8: Build + run the affected tests**

Build: `dotnet build ModernWigiDash.slnx -c Release --nologo`
Tests (temp output, service running): `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: build green; `ServiceHostSmokeTests`, `WcfDisplayServiceTests`, `WcfClientServerConsistencyTests` (contract drift guard), and `ServiceContractTests` (DTO round-trip) all pass.

- [ ] **Step 9: Commit**

```bash
git add -A ModernWigiDash.Service.Contracts/IModernWigiDashDisplayServiceContract.cs ModernWigiDash.Service/Wcf/ModernWigiDashDisplayService.cs ModernWigiDash.Service.Contracts/ModernWigiDashDisplayServiceClient.cs ModernWigiDash.Service/Program.cs ModernWigiDash.Tests/ServiceHostSmokeTests.cs ModernWigiDash.Tests/WcfDisplayServiceTests.cs ModernWigiDash.Service/ModernWigiDash.Service.csproj Directory.Packages.props
git commit -m "refactor(sensors): remove WCF GetSensorSnapshot + LhmSensorReader (ADR-0004)"
```

---

### Task 5: Full-suite verification + docs sync

**Files:**
- Verify: full test suite.
- Modify: `CONTEXT.md` (already drafted during planning — apply/confirm the sensor producer + Service term edits; see the ADR's Consequences).

**Interfaces:**
- Consumes: everything above.
- Produces: green suite + docs matching the implemented reality.

- [ ] **Step 1: Run the full suite**

`dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: all existing tests pass (295 baseline − removed sensor tests + new `LhmSharedMemoryReaderTests`), including `TelemetryStoreMappingTests` (uses `UpdateFromDto` — unchanged) and `WcfClientServerConsistencyTests` (contract drift guard now reflects the removed op on both sides).

- [ ] **Step 2: Confirm CONTEXT.md reflects the implemented change**

The glossary `LibreHardwareService producer` term, the `Service.Contracts` "no sensor operation remains" note, and data-flow item 3 must match what shipped (reader reads shared memory → `UpdateFromDto`). Fix any drift from the drafted text.

- [ ] **Step 3: Final build sanity**

`dotnet build ModernWigiDash.slnx -c Release --nologo` — `0 Error(s)`, `0 Warning(s)` unless pre-existing.

- [ ] **Step 4: Commit (if docs changed)**

```bash
git add CONTEXT.md
git commit -m "docs(sensors): sync CONTEXT.md with LHS shared-memory sensor path (ADR-0004)"
```
