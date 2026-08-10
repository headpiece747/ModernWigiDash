# ModernWigiDash — Domain Glossary & Architecture

## What this project is

ModernWigiDash is a .NET 10 WPF application that drives a USB-connected small LCD display (G.Skill WigiDash, VID 0x28DA PID 0xEF01, 1016×592 pixels, RGB565 framebuffer). The application composites customizable widgets (clocks, weather, hardware telemetry, Twitch chat, stock tickers, etc.) onto pages and streams the rendered frames to the display over USB HID/WinUSB.

## Domain Glossary

### Core Concepts

| Term | Definition |
|------|-----------|
| **WigiDash display** | The physical 1016×592 RGB565 LCD connected via USB HID (WinUSB or LibUsbDotNet). Manufactured by G.Skill. |
| **Widget** | A self-contained UI component displayed on the WigiDash canvas. Each widget has a metadata attribute (`[WidgetMetadata]`), renders via SkiaSharp to an `SKCanvas`, and receives touch input. Widgets are registered by reflection and instantiated by `WidgetPluginLoader`. |
| **Page** | A layout container holding placed widgets. The profile holds ordered pages; users swipe between pages at runtime. |
| **PlacedWidgetInstance** | A widget bound to a position on a page — holds coordinates, size, rotation, opacity, and the widget instance. |
| **Profile** | The persisted set of pages, active page index, and widget placements. |
| **Frame** | A pixel buffer (SKBitmap) composited by `SkiaFrameCompositor`, converted to RGB565, and streamed to the display over USB. |
| **FrameSink** | A destination for composited frames. `IFrameSink` exposes `SendFrame(SKBitmap)` + `IsReady`, returning a truthful `FrameDeliveryResult`. The App binds one `FrameDelivery` instance to the direct-USB engine (the WCF sink and `FrameSinkRouter` were removed with the Service, ADR-0005). |
| **FrameDelivery** | The single frame-delivery policy module (Sdk): bounded DropOldest channel → drain-to-latest → paced send, owning encode, pooled exact-size buffers, and drop accounting. One entry point feeds the policy: `Push(SKBitmap)` (encode + pool); a `FrameDelivery.Create` factory makes the required encode/pool/send seams unrepresentable at production bind sites (the ctor remains for tests exercising unconfigured readiness). The App's direct-USB sink is its one runtime instance; pacing defaults to 33ms, the USB engine's device-capability rate. Drop counting lives inside the module. |
| **DisplayGeometry** | The WigiDash framebuffer geometry (Sdk) — the single source of truth for the active pixel area (1016×592, 2 bytes/pixel, payload size). Hardware's `DisplayProtocolConstants` aliases these values and Core's compositor derives its buffer from them, so the pixel area can never drift between projects. |
| **Render tick** | The 30 FPS `DispatcherTimer` in MainWindow that calls `Compositor.Compose()`, converts the frame to RGB565, and queues it for delivery. |

### Hardware / Transport

| Term | Definition |
|------|-----------|
| **Transport** | The USB HID communication layer. `DisplayHidTransport` picks one **ITransferBackend** in `Connect` — WinUSB (primary) or LibUsbDotNet (fallback) — and runs its connect/init/frame/touch policy against that seam; the backends own the device-specific transfers and teardown (WinUsbBulkDevice implements the seam directly, LibUsbTransferBackend wraps the chunked-write path). `IDisplayTransport` exposes only the live surface (Connect, SendFrame, ReadTouch, IsConnected, GoToStandby). All operations are synchronous — no fake async wrappers. |
| **ITransferBackend** | The transport's USB transfer seam (Hardware): vendor control in/out + bulk write. Two real adapters (WinUSB, LibUsbDotNet) plus test fakes, so the transport's policy is drivable without hardware and the backend choice is made once in `Connect` instead of re-decided per call. |
| **ConnectionState** | The engine's single connection truth (Hardware): `Disconnected` / `Connecting` / `Connected` / `Simulated` — one value instead of the old lockstep IsConnected/IsHardwareActive/IsSimulationMode trio callers disagreed on. The presenter gate and the USB badge read the same state. |
| **DisplayDeviceEngine** | The App's hardware engine (Hardware): owns transport connection, standby, and the 16ms touch poll. The constructor is deliberately inert — no connect attempt, no background loops (construction never reaches for hardware, so test hosts are safe); `Start()` begins the initial connect, the 5s reconnect timer, and touch polling. |
| **FramePump** | The 30 FPS presentation cadence module (App): dispatcher timer that composes the active page, hands the frame to the presenter, and requests a repaint — the window keeps only the WPF draw, so the buffer drawn is exactly the buffer sent. Badge refreshes ride the tick as an injected callback. |
| **FrameEncoder** | Converts SKBitmap (RGBA) → RGB565 little-endian byte array for the display's framebuffer format. Handles scaling when the source bitmap isn't exactly 1016×592. |
| **TouchReport** | Hardware touch input from the display: type (Down/Up), X/Y coordinates. Polled by the App's `DisplayDeviceEngine` at 16ms in direct-USB mode and normalized once via the shared `TouchReport.ToEventType` (the single mapping site, also used by the deleted Service seam). The App never sees vendor protocol bytes. |
| **DisplayProtocolConstants** | USB vendor commands, framebuffer dimensions, and protocol constants for the WigiDash hardware. |
| **WinUsbBulkDevice** | Raw WinUSB P/Invoke wrapper for bulk and control transfers. Owned lifecycle (opens SetupAPI handle, initializes WinUSB, manages pipe timeouts). |

### Service Architecture

> The Windows Service was removed (ADR-0005). Terms below are kept only for
> historical context; the repo no longer contains a Service project, a WCF
> contract/client, or any service-routing machinery.

| Term | Definition |
|------|-----------|
| **Service** | ~~A Windows Service (`ModernWigiDashService`) running as LocalSystem that owns the USB device, captures telemetry, and exposes a WCF endpoint.~~ **Removed (ADR-0005)**: its frame-time role lives in PresentMon Service, its sensor role in LibreHardwareService, and the app talks USB directly. |
| **Service.Contracts** | ~~Shared assembly with the WCF contract and client.~~ **Removed (ADR-0005)** — its live telemetry DTOs (`SensorSnapshotDto`, `SensorReadingDto`, `FrameTimeSnapshotDto`) moved into `ModernWigiDash.Sdk` as plain data models. |
| **DetectServicePort** | ~~Protocol-verified service discovery.~~ Removed with the service (ADR-0005). |
| **ServiceRoutingState** | ~~Owns the App↔service routing truth.~~ Removed with the service (ADR-0005); poll loops are all direct producers now. |
| **ProfileOps** | The pure profile-operations module (Core): page CRUD, widget placement/rehydration (`PlaceWidget`, `PlaceCentered`), and JSON export/import. MainWindow keeps only dialogs, selection, and refresh — the user-visible model mutations are testable through the module's interface. |
| **PollLoop** | One parameterized poll loop (Sdk): owns its cancellation lifecycle, readiness guard, failure logging, and inter-tick delay. The App's two direct producers (sensor 1s, frame-time 1s) and the engine's direct-USB touch loop (16ms) are all instances with injected probes and sample sinks — one loop shape, every hop. |
| **Standby** | The display's idle state: the built-in vendor Welcome screen. Entered on the app-close path — the engine's `Dispose` sends `GoToStandby`. After standby, heartbeats stop, so the display also sleeps on its own timeout. |

### Telemetry & Data Flow

| Term | Definition |
|------|-----------|
| **LhmSensorStore** | Static in-process cache of hardware sensor readings from LibreHardwareService. A facade over `StaticTelemetryStore<LhmSnapshot>` (Sdk) — the shared base holds the staleness/read/reset surface, the facade keeps only the DTO→record mapping. Written by the App's shared-memory producer (`LhmSharedMemoryReader` → `UpdateFromDto`, producer timestamp preserved); read by `HardwareMonitorWidget` through `TryReadFresh` — the store owns the staleness decision, consumers cannot skip it. |
| **FrameTimeStore** | Static in-process cache of FPS/frame-time snapshots. A facade over `StaticTelemetryStore<FrameTimeSnapshotDto>` (Sdk) with the same shape as `LhmSensorStore` — the snapshot shape is the DTO itself (no shadow record). Written by the PresentMon producer (see below) via `FrameTimeStore.UpdateFromDto` (producer timestamp preserved); read by `FrameTimeWidget` through `TryReadFresh`. The widget renders only PresentMon-reported data: an 8-metric dashboard view (FPS/frame time hero, 1% low, 0.1% low, CPU frame, GPU busy %, displayed FPS, dropped frames, GPU time, present mode) that tap-toggles to a PresentMon-overlay-style readout; both views shrink gracefully and show "—" values when no process is tracked (no monitor-refresh-rate display — that is not a PresentMon feature). |
| **StaticTelemetryStore** | The shared store-facade base (Sdk): owns one `TelemetryStore<TRecord>` instance bound to the domain's empty value and staleness window, exposing read/freshness/update/reset. `LhmSensorStore` and `FrameTimeStore` wrap one instance each — the staleness policy and its test surface are declared exactly once. |
| **PresentMon producer** | The frame-time source (ADR-0003). Replaces the deleted `FrameTimeReader`: the app connects to PresentMon Service (`pmOpenSession`, non-elevated), resolves a target PID (preferred foreground window, else most-active presenter), calls `pmStartTrackingProcess`, and polls a rolling 1s dynamic query (`PM_METRIC_PRESENTED_FPS` AVG/P99/P01, `CPU_FRAME_TIME`, `GPU_TIME`, `GPU_BUSY`, `APPLICATION`) on the existing 1s `PollLoop` shape. Results map into `FrameTimeSnapshotDto` → `FrameTimeStore.Update`. Loads `PresentMonAPI2.dll` from the service SDK dir at runtime — never ships its own copy (client↔service binary protocol isn't backward-guaranteed, issue #383). Absent service ⇒ widget shows a graceful "PresentMon not installed" state. |
| **TrackedTargetResolver** | The PresentMon tracking-target selection (App/PresentMon): which process to track — the preferred foreground window, else the most-active presenter — with injected foreground/children probes (real user32/toolhelp adapters, test fakes) so resolution is drivable without real processes. |
| **LibreHardwareService producer** | The hardware-sensor source (ADR-0004). Replaces the deleted `LhmSensorReader`: LibreHardwareService (LocalSystem) owns the hardware polling and publishes readings to named shared-memory maps (`sensors`, `status`; `all-hardware` optional) guarded by named mutexes. The App's `LhmSharedMemoryReader` opens the maps by name, takes the mutex per read, parses the header (`MetaDataSize`, `UpdateInterval`, `LastUpdate`, `index-length/offset`, `index-format`, `data-length/offset`) and honors the declared index format (JSON or MessagePack — MessagePack-CSharp package). DataSensor records map 1:1 into `SensorSnapshotDto` (SensorId preserved; LHS publishes value/min/max only — no Avg; `UnitFor(SensorType)` replicated app-side) → `LhmSensorStore.UpdateFromDto` on the existing 1s `PollLoop` shape. Absent service ⇒ widget shows a graceful "LibreHardwareService not running" state. |
| **UpdateFromDto** | Maps sensor DTOs to widget-side records on the store. Centralizes the DTO→render-model translation in the store layer. |

### Widgets

| Term | Definition |
|------|-----------|
| **Widget metadata** | `[WidgetMetadataAttribute]` defines the plugin ID, display name, category, and default grid size. Read by `WidgetPluginLoader` for catalog registration. |
| **Widget property** | `[WidgetPropertyAttribute]` on a widget's public property defines the inspector UI (text/number/boolean/color/choice). Widgets implement `IWidgetPropertyOptionsProvider` for dynamic choice lists. |
| **Widget action** | `IWidgetActionInvoker` / `IWidgetActionPresentationProvider` — widgets that expose executable actions (e.g., Twitch login, hotkey execution). |
| **Inspector panel** | The right-side settings panel in MainWindow; populates UI from `[WidgetProperty]` attributes and calls `ApplyInspectorPropertyValue` to write values back. The reflection→editor mapping is a pure `EditorDescription` model (no WPF, no dialogs); a thin WPF mapper renders it, and write-back funnels through the single `ApplyInspectorPropertyValue` seam. Dynamic choice lists resolve through `IWidgetPropertyOptionsProvider` — no widget-specific `typeof` checks. |
| **TextRenderHelper** | Shared rendering utilities: text truncation with ellipsis, centered text drawing, title/subtitle placeholders, sparkline charts, SVG path caching. |
| **PriceFeedManager** | Shared WebSocket-based price streaming for stock/crypto/FX tickers. One crypto symbol table (alias → base coin + CoinGecko id) behind an `IWebSocketFeed` seam — Binance/Finnhub feed adapters plus in-memory fakes in tests; Yahoo/CoinGecko REST fallbacks share the process-wide HttpClient, which the manager never owns. |
| **IWebSocketFeed** | The WebSocket seam (Widgets) behind the price feeds and the Twitch IRC loop: connect, send text, receive one complete text message per await, abort. `ClientWebSocketFeed` is the production adapter; in-memory fakes drive both consumers' loops in tests — one framing implementation per assembly. |
| **IWidgetIconGrab** | Widget contract for edit-mode icon dragging (Core): the widget owns its icon geometry — hit region, center, and grab-move math — so the input module sees only the capability, never the widget type. Mirrors the `ResizeHandleSize` ownership pattern. Hit-testing runs in the widget's rotated-local space (`PlacedWidgetInstance.ToLocalPoint` — the render-transform inverse), so a rotated icon's grab region matches its drawn footprint. Offsets persist through `ModernWidgetBase.SetProperty`. |
| **TelemetryProducers** | The telemetry producer cluster (App): owns the two direct poll loops (sensor 1s via LibreHardwareService shared memory, frame-time 1s via PresentMon), their producers/readers, and the error-dedup state. The window keeps one `Start()`/`Dispose()`; the cluster is testable without WPF. |
| **LogCadence** | The diagnostic log-cadence rule (Sdk): fires on the first call and/or every Nth — one tested rule replaces the hand-rolled modulo counters that once mirrored each other across the transport, the backend, and the delivery pipeline. |
| **SetProperty / PersistProperty** | The single widget-property bookkeeping invariant (Sdk/App): a widget property lives in both the instance property and the placed instance's `PropertyValues` (the export format). `ModernWidgetBase.SetProperty` writes instance + `OnPropertyChanged` + persistence; the App's context (`PersistProperty`) resolves the owning placed instance by identity. Every mutation path — inspector write-back, icon-grab moves, widget `OnTouch` toggles — routes through it, so export round-trips cannot silently lose runtime toggles. |
| **FeedLoop** | One WebSocket reconnect-loop shape (Widgets), in the `PollLoop` image: create feed → connect → onConnected → read messages until closed → backoff delay, repeating until cancelled. PriceFeedManager's Binance/Finnhub loops and the Twitch IRC loop are instances with injected `IReconnectPolicy` (fixed vs exponential backoff) and status/error hooks. |
| **IAudioCaptureSource** | The audio-capture seam behind the visualizer (Widgets): a source of float samples from the system output, drivable by an in-memory fake in tests. `WasapiLoopbackCaptureSource` is the NAudio adapter; `AudioSpectrumAnalyzer` (pure binning/smoothing/ring buffer) sits between capture and rendering. The widget renders snapshots; it never touches WASAPI. |

### Theme

| Term | Definition |
|------|-----------|
| **ThemeSettings** | Serializable theme definition. Colors stored as `#RRGGBB` hex. Lazy-loaded from `app_theme.json`; applies to WPF chrome (surfaces, accents, text). |
| **ThemeManager** | Pushes `ThemeSettings` into WPF application resources. |

### Touch Input

| Term | Definition |
|------|-----------|
| **Hardware touch** | Physical touch from the display, polled by `DisplayDeviceEngine` at 16ms in direct-USB mode, normalized once via `TouchReport.ToEventType`, and raised through `OnTouchEvent` to MainWindow. |
| **Widget touch** | Local-coordinate touch events delivered to `IModernWidget.OnTouch()` after compositor hit-testing. |
| **Gesture** | Input-sequence interpretation shared by the USB-direct and mouse paths: one `GestureInterpreter` state machine (swipe 70/80 px for page nav, arrow-tap 60/964 edge zones × 200–400 y-band for page switch, tap for widget touch). The mouse feeds the same Down/Move/Up vocabulary through `InputController.Feed` directly; edit-mode widget manipulation gates the machine in the mouse handlers (widget routing suppressed in edit mode, page actions still applied). |
| **InputController** | The single input module (App) behind which the gesture machine, its outcome application, the routing veto, widget routing, and edit-mode manipulation decisions (drag/resize/icon-grab + snap-to-grid math) all live. Callers — mouse handlers and hardware touch — only feed `Down/Move/Up` coordinates + a suppression flag; page-switch UI work stays in MainWindow behind one navigation seam. The suppression flag is a property of the *source*: the mouse passes the desktop edit-mode flag (authoring input — presses manipulate), the physical display passes false (runtime input — hotkeys fire on the device even in edit mode). |

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│  ModernWigiDash.App (WPF host)                          │
│  MainWindow + partials (Context, ServiceIntegration)     │
│  Compositor → RGB565 → FrameDelivery → direct USB        │
│  Touch: engine 16ms poll → gesture detection → widgets   │
│  Producers: LHS shared memory (sensors), PresentMon      │
└──────────────────┬──────────────────────────────────────┘
                   │ Direct USB (WinUSB)
┌──────────────────▼──────────────────────────────────────┐
│  ModernWigiDash.Hardware                                │
│  DisplayHidTransport (USB HID) + DisplayDeviceEngine    │
│  Transport/ (FrameEncoder, TouchReport, WinUSB, SetupAPI)│
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  ModernWigiDash.Widgets                                 │
│  12 widget implementations                              │
│  Stores (LhmSensorStore, FrameTimeStore)                │
│  PriceFeedManager, Twitch, TextRenderHelper              │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  ModernWigiDash.Core                                    │
│  Compositor, Theming, Rendering, Telemetry, Models      │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  ModernWigiDash.Sdk                                     │
│  Contracts (IModernWidget, IModernWigiDashContext)       │
│  Attributes (WidgetMetadata, WidgetProperty)            │
│  Data models (SensorSnapshotDto, FrameTimeSnapshotDto)  │
│  FrameDelivery, PollLoop, FileLog                       │
└─────────────────────────────────────────────────────────┘
```

### Data Flow

1. **Render loop**: MainWindow tick → `SkiaFrameCompositor.Compose()` → `FrameDelivery` (encode + pool + pace) → `DisplayDeviceEngine.SendFrameBytes()` → `IDisplayTransport.SendFrame()`
2. **Touch flow**: `DisplayDeviceEngine.TouchPollTick` (16ms PollLoop, reads `ReadTouch()`, normalizes raw byte → `TouchEventType` via `TouchReport.ToEventType`) → `OnTouchEvent` → MainWindow → `SkiaFrameCompositor.RouteTouch()` → `IModernWidget.OnTouch()`
3. **Sensor flow**: `LhmSharedMemoryReader` (App, reads LibreHardwareService shared memory on the 1s `PollLoop` shape) → `LhmSensorStore.UpdateFromDto()` (static cache) → `HardwareMonitorWidget.Render()` reads snapshot
4. **Frame-time flow**: PresentMon producer (ADR-0003: `pmOpenSession` → `pmStartTrackingProcess(pid)` → dynamic-query poll on the 1s `PollLoop` shape) → `FrameTimeStore.Update()` (static cache) → `FrameTimeWidget.Render()` reads snapshot

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Synchronous transport interface | USB I/O (WinUSB/LibUsbDotNet) is inherently blocking; fake async adds no value and forces sync-over-async bridges |
| Static stores with LastUpdate freshness | Widgets are instantiated via reflection (parameterless ctor) and can't receive injected dependencies; static stores with staleness tracking are the pragmatic solution |
| Telemetry DTOs in Sdk | `SensorSnapshotDto`/`SensorReadingDto`/`FrameTimeSnapshotDto` live in the lowest common layer so every project shares one mailbox format (the Service.Contracts assembly was removed with the service, ADR-0005) |
| FileLog in Sdk | Shared file logging utility placed in the lowest common layer so all projects can log to `display_device.log` without circular dependencies |
| Widget-per-file convention | Each widget class lives in its own `.cs` file with its `[WidgetMetadata]` attribute; the catalog is discovered by scanning the assembly |

### Testing

- 528 unit tests covering protocol framing, RGB565 encoding, DTO mapping, telemetry store freshness, touch routing/normalization, frame delivery/coalescing, widget property defaults + persistence round-trips, price-feed lifecycle and WebSocket-seam behavior, transport policy through the ITransferBackend seam (including the connect fallback policy), FramePump cadence, IRC-loop behavior through the feed seam, audio DSP + capture lifecycle, icon-grab geometry, page-tab rules, telemetry-producer ticks, cached color parsing, stopwatch timing, feed subscription lifecycle, log-cadence and log-on-change rules, dialog hosts, inspector write-back, profile-import sanitization caps (including null collections), and present-mon blob parsing
- `DisplayProtocolTests` — widget config layout + RGB565 encoding (BGRA framebuffer format)
- `DisplayDeviceEngineTests` — direct-USB touch polling (normalized events, null-report skip), touch type normalization, protocol constants, dispose safety
- `LhmSharedMemoryReaderTests` — LHS map parsing (JSON + MessagePack index), unit table, malformed-input fallbacks
- `TelemetryStoreMappingTests` — DTO→store mapping, freshness tracking, null-DTO handling
- `TwitchTokenStoreTests` — DPAPI round-trip, overwrite, delete isolation
- `ThemeManagerTests` — WPF resource application (STA thread), lazy theme load
- `ThemeSettingsTests` — hex color parsing, JSON round-trip, metadata coverage
- `PriceFeedManagerLifecycleTests` — subscription/unsubscription lifecycle and GetPrice seam behavior
- `PriceFeedSocketLoopTests` — WebSocket-seam loop behavior (ticker apply, reconnect) and the single crypto symbol table's CoinGecko invariant
- `DisplayHidTransportTests` — transport policy (init sequence, frame framing, touch parsing) through the ITransferBackend seam
- `FramePumpTests` — 30 FPS cadence wiring on a live STA/Dispatcher
- `TwitchChatStreamLoopTests` — IRC loop behavior (handshake, reconnect backoff, PRIVMSG parsing) through the feed seam
- `FontAndTextTests` / `GriddyIconsTests` / `HotkeyActionTests` — subject files split out of the old UnitTestSuite grab-bag (the suite now holds the remaining core-infrastructure tests; widget tests live with their widgets)
- Shared test doubles in `TestDoubles.cs` — one `TestContext` / `FakeFeed` / `StubHttpHandler` / `StubPresentMonNative` per seam instead of a per-file copy

## Architecture Decisions

| ADR | Decision | Rationale |
|-----|----------|-----------|
| [ADR-0001](docs/adr/0001-synchronous-transport-interface.md) | Synchronous transport interface | USB I/O is inherently blocking; fake async adds cognitive overhead and prevents compile-time detection of sync-over-async |
| [ADR-0003](docs/adr/0003-presentmon-service-for-frame-time.md) | PresentMon Service as frame-time source | Replaces the in-house ETW reader; Intel's LocalSystem service + named-pipe client matches the non-elevation goal with no custom ETW capture to maintain |
| [ADR-0004](docs/adr/0004-librehardwareservice-shared-memory-for-sensors.md) | LibreHardwareService shared memory as hardware sensor source | Replaces the in-house WCF sensor reader; LHS's LocalSystem service + mutex-guarded shared-memory maps match the non-elevation and low-overhead goals with no custom hardware-polling code to maintain |
| [ADR-0005](docs/adr/0005-remove-windows-service.md) | Remove the ModernWigiDash Windows Service | Once telemetry came from PresentMon/LibreHardwareService and the app owned the USB device directly, the Service and its WCF pipe were dead weight — a whole project, a named-pipe attack surface, and install machinery to maintain |
