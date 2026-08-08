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
| **Frame** | A pixel buffer (SKBitmap) composited by `SkiaFrameCompositor`, converted to RGB565, and streamed to the display over USB or WCF. |
| **FrameSink** | A destination for composited frames. `IFrameSink` exposes `SendFrame(SKBitmap)` + `IsReady`, returning a truthful `FrameDeliveryResult`. Each `FrameDelivery` instance is a sink for one transport (WCF, direct USB); `FrameSinkRouter` picks the first-ready sink per render tick and owns the WCF-retry trigger. |
| **FrameDelivery** | The single frame-delivery policy module (Sdk): bounded DropOldest channel → drain-to-latest → paced send, owning encode, pooled exact-size buffers, and drop accounting. Two entry points feed one policy: `Push(SKBitmap)` (encode + pool) and `PushBytes(byte[])` (service hop). Every transport hop — App WCF sink, App direct-USB engine, Service `RunFrameLoop` — is an instance behind the same interface, so backlog behaves identically in every mode. Pacing default 33ms (the USB engine's device-capability rate); the two WCF hops set it to zero — the pipe round-trip already bounds them, and pacing there adds display-visible latency to page switches. Drop counting lives inside the module. |
| **Render tick** | The 30 FPS `DispatcherTimer` in MainWindow that calls `Compositor.Compose()`, converts the frame to RGB565, and queues it for delivery. |

### Hardware / Transport

| Term | Definition |
|------|-----------|
| **Transport** | The USB HID communication layer. `DisplayHidTransport` handles WinUSB (primary) and LibUsbDotNet (fallback) for control transfers, bulk writes, and touch reads. All operations are synchronous — no fake async wrappers. |
| **FrameEncoder** | Converts SKBitmap (RGBA) → RGB565 little-endian byte array for the display's framebuffer format. Handles scaling when the source bitmap isn't exactly 1016×592. |
| **TouchReport** | Hardware touch input from the display: type (Down/Up), X/Y coordinates. Polled by the worker, normalized once at the service transport seam (`RunTouchPollLoop` maps the raw protocol byte to `TouchEventType`), and routed through a channel to MainWindow. The App never sees vendor protocol bytes. |
| **DisplayProtocolConstants** | USB vendor commands, framebuffer dimensions, and protocol constants for the WigiDash hardware. |
| **WinUsbBulkDevice** | Raw WinUSB P/Invoke wrapper for bulk and control transfers. Owned lifecycle (opens SetupAPI handle, initializes WinUSB, manages pipe timeouts). |

### Service Architecture

| Term | Definition |
|------|-----------|
| **Service** | A Windows Service (`ModernWigiDashService`) running as LocalSystem that owns the USB device, captures telemetry, and exposes a WCF endpoint. **Currently isolated** (ADR-0003, ADR-0004): kept in the repo but not used at runtime — its frame-time role moved to PresentMon Service, its sensor role moved to LibreHardwareService, and the app talks USB directly. Revisit if a future plan needs it. |
| **WCF endpoint** | `net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc` — the CoreWCF service contract for IPC between the service and the app, hosted over a named pipe (kernel-level ACL security; no TCP exposure). |
| **Service.Contracts** | Shared assembly containing the WCF contract interface (`IModernWigiDashDisplayServiceContract`), DTOs (`FrameTimeSnapshotDto`, `SensorSnapshotDto`), data models, and the WCF client (`ModernWigiDashDisplayServiceClient`). No Hardware dependency — purely a contract library. No sensor operation remains (removed by ADR-0004). |
| **DetectServicePort** | Protocol-verified service discovery: probes known named pipe endpoints → WCF GetVersion handshake. An impostor pipe cannot hijack frames without speaking the contract. |
| **ProfileOps** | The pure profile-operations module (Core): page CRUD, widget placement/rehydration, and JSON export/import. MainWindow keeps only dialogs, selection, and refresh — the user-visible model mutations are testable through the module's interface. |
| **ServiceRoutingState** | Owns the App↔service routing truth (App): whether the service is active, the consecutive-failure counting that flips it (default 2), and the throttled re-detect trigger (10s). Poll loops read `IsServiceActive` as their readiness guard and report failures here — a service that dies after a successful connect stops the loops within a couple of failures instead of hammering a faulted channel. |
| **PollLoop** | One parameterized poll loop (Sdk): owns its cancellation lifecycle, readiness guard, failure logging, and inter-tick delay. The App's three WCF producers (touch 16ms, sensor 1s, frame-time 1s) and the Service's touch+keepalive loop are all instances with injected probes and sample sinks — one loop shape, every hop. |
| **Standby** | The display's idle state: the built-in vendor Welcome screen. Entered on every owner-exit path — app close (via the WCF Shutdown op, or the engine's Dispose in direct-USB mode) and service stop (`DisplayHardwareWorkerService.StopAsync`). After standby, heartbeats stop, so the display also sleeps on its own timeout. |

### Telemetry & Data Flow

| Term | Definition |
|------|-----------|
| **LhmSensorStore** | Static in-process cache of hardware sensor readings from LibreHardwareService. Written by the App's shared-memory producer (`LhmSharedMemoryReader` → `UpdateFromDto`, producer timestamp preserved); read by `HardwareMonitorWidget` through `TryReadFresh` — the store owns the staleness decision, consumers cannot skip it. Same staleness semantics. |
| **FrameTimeStore** | Static in-process cache of FPS/frame-time snapshots from the ETW reader. Written by the service's frame-time reader (producer timestamp preserved); read by `FrameTimeWidget` through `TryReadFresh`. Same staleness semantics. |
| **FrameTimeReader** | Background service that captures ETW present events (DXGI/D3D9/DxgKrnl) and builds `FrameTimeSnapshotDto` records. |
| **LibreHardwareService producer** | The hardware-sensor source (ADR-0004). Replaces the deleted `LhmSensorReader`: LibreHardwareService (LocalSystem) owns the hardware polling and publishes readings to named shared-memory maps (`sensors`, `status`; `all-hardware` optional) guarded by named mutexes. The App's `LhmSharedMemoryReader` opens the maps by name, takes the mutex per read, parses the header (`MetaDataSize`, `UpdateInterval`, `LastUpdate`, `index-length/offset`, `index-format`, `data-length/offset`) and honors the declared index format (JSON or MessagePack — MessagePack-CSharp package). DataSensor records map 1:1 into `SensorSnapshotDto` (SensorId preserved; `Avg` dropped to 0 — LHS publishes value/min/max only; `UnitFor(SensorType)` replicated app-side) → `LhmSensorStore.UpdateFromDto` on the existing 1s `PollLoop` shape. Absent service ⇒ widget shows a graceful "LibreHardwareService not running" state. |
| **UpdateFromDto** | Maps sensor DTOs to widget-side records on the store. Centralizes the DTO→render-model translation in the store layer. |

### Widgets

| Term | Definition |
|------|-----------|
| **Widget metadata** | `[WidgetMetadataAttribute]` defines the plugin ID, display name, category, and default grid size. Read by `WidgetPluginLoader` for catalog registration. |
| **Widget property** | `[WidgetPropertyAttribute]` on a widget's public property defines the inspector UI (text/number/boolean/color/choice). Widgets implement `IWidgetPropertyOptionsProvider` for dynamic choice lists. |
| **Widget action** | `IWidgetActionInvoker` / `IWidgetActionPresentationProvider` — widgets that expose executable actions (e.g., Twitch login, hotkey execution). |
| **Inspector panel** | The right-side settings panel in MainWindow; populates UI from `[WidgetProperty]` attributes and calls `ApplyInspectorPropertyValue` to write values back. The reflection→editor mapping is a pure `EditorDescription` model (no WPF, no dialogs); a thin WPF mapper renders it, and write-back funnels through the single `ApplyInspectorPropertyValue` seam. Dynamic choice lists resolve through `IWidgetPropertyOptionsProvider` — no widget-specific `typeof` checks. |
| **TextRenderHelper** | Shared rendering utilities: text truncation with ellipsis, centered text drawing, title/subtitle placeholders, sparkline charts, SVG path caching. |
| **PriceFeedManager** | Shared WebSocket-based price streaming for stock/crypto/FX tickers. Binance (crypto WebSocket), Finnhub (stock WebSocket), Yahoo Finance REST fallback, CoinGecko REST fallback. |

### Theme

| Term | Definition |
|------|-----------|
| **ThemeSettings** | Serializable theme definition. Colors stored as `#RRGGBB` hex. Lazy-loaded from `app_theme.json`; applies to WPF chrome (surfaces, accents, text). |
| **ThemeManager** | Pushes `ThemeSettings` into WPF application resources. |

### Touch Input

| Term | Definition |
|------|-----------|
| **Hardware touch** | Physical touch from the display, polled by DisplayHardwareWorkerService, normalized once at the service transport seam and routed through a `Channel<TouchEventInfo>` to MainWindow. |
| **Widget touch** | Local-coordinate touch events delivered to `IModernWidget.OnTouch()` after compositor hit-testing. |
| **Gesture** | Input-sequence interpretation shared by the USB-direct, WCF, and mouse paths: one `GestureInterpreter` state machine (swipe 70/80 px for page nav, arrow-tap 60/964 edge zones × 200–400 y-band for page switch, tap for widget touch). The mouse is fed through the same Down/Move/Up vocabulary via `FeedMouseGesture`; edit-mode widget manipulation gates the machine in the mouse handlers (widget routing suppressed in edit mode, page actions still applied). |
| **InputController** | The single input module (App) behind which the gesture machine, its outcome application, the routing veto, widget routing, and edit-mode manipulation decisions (drag/resize/icon-grab + snap-to-grid math) all live. Callers — mouse handlers, hardware touch, WCF touch — only feed `Down/Move/Up` coordinates + a suppression flag; page-switch UI work stays in MainWindow behind one navigation seam. The suppression flag is a property of the *source*: the mouse passes the desktop edit-mode flag (authoring input — presses manipulate), the physical display passes false (runtime input — hotkeys fire on the device even in edit mode). |

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│  ModernWigiDash.App (WPF host)                          │
│  MainWindow + partials (Context, ServiceIntegration)     │
│  Compositor → RGB565 → Channel → WCF client             │
│  Touch routing → gesture detection → widget dispatch     │
└──────────────────┬──────────────────────────────────────┘
                   │ WCF (named pipe)
┌──────────────────▼──────────────────────────────────────┐
│  ModernWigiDash.Service.Contracts                       │
│  Contract + DTOs + Client (no Hardware dependency)      │
└──────────────────┬──────────────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────────────┐
│  ModernWigiDash.Service                                 │
│  DisplayService (WCF host) + Workers (sensor, frame, LHM)│
│  References: Core, Hardware, Sdk, Service.Contracts     │
└──────────────────┬──────────────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────────────┐
│  ModernWigiDash.Hardware                                │
│  DisplayHidTransport (USB HID) + DisplayDeviceEngine    │
│  Transport/ (FrameEncoder, TouchReport, WinUSB, SetupAPI)│
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  ModernWigiDash.Widgets                                 │
│  15+ widget implementations                             │
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
│  Base types (ModernWidgetBase, GridSizeExtensions)      │
└─────────────────────────────────────────────────────────┘
```

### Data Flow

1. **Render loop**: MainWindow tick → `SkiaFrameCompositor.Compose()` → `FrameEncoder.ConvertToRgb565()` → Channel → `DisplayHardwareWorkerService.RunFrameLoop()` → `IDisplayTransport.SendFrame()`
2. **Touch flow**: `DisplayHardwareWorkerService.RunTouchPollLoop()` (normalizes raw byte → `TouchEventType`) → `Channel<TouchEventInfo>` → MainWindow → `SkiaFrameCompositor.RouteTouch()` → `IModernWidget.OnTouch()`
3. **Sensor flow**: `LhmSharedMemoryReader` (App, reads LibreHardwareService shared memory on the 1s `PollLoop` shape) → `LhmSensorStore.UpdateFromDto()` (static cache) → `HardwareMonitorWidget.Render()` reads snapshot
4. **Frame-time flow**: `FrameTimeReader` (ETW BackgroundService) → `FrameTimeStore.Update()` (static cache) → `FrameTimeWidget.Render()` reads snapshot

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Synchronous transport interface | USB I/O (WinUSB/LibUsbDotNet) is inherently blocking; fake async adds no value and forces sync-over-async bridges |
| Static stores with LastUpdate freshness | Widgets are instantiated via reflection (parameterless ctor) and can't receive injected dependencies; static stores with staleness tracking are the pragmatic solution |
| Service.Contracts separation | WCF contract+client+DTOs live in a separate assembly without Hardware dependency, so the App can reference only the contracts |
| FileLog in Sdk | Shared file logging utility placed in the lowest common layer so all projects can log to `display_device.log` without circular dependencies |
| Widget-per-file convention | Each widget class lives in its own `.cs` file with its `[WidgetMetadata]` attribute; the catalog is discovered by scanning the assembly |

### Testing

- 295 unit tests covering protocol framing, RGB565 encoding, DTO mapping, telemetry store freshness, WCF contract consistency, touch routing, frame-sink routing/coalescing, widget property defaults, price-feed lifecycle, profile-import sanitization caps, and WCF service-lifecycle (PerCall vs singleton state) regressions
- `DisplayProtocolTests` — widget config layout + RGB565 encoding (BGRA framebuffer format)
- `WcfClientServerConsistencyTests` — reflection-based contract drift guard (service implements all members, client wraps all operations)
- `WcfDisplayServiceTests` — channel behavior, null reader fallback, frame queueing
- `ServiceContractTests` — DTO round-trip, default values, DataContract attributes
- `TelemetryStoreMappingTests` — DTO→store mapping, freshness tracking, null-DTO handling
- `TwitchTokenStoreTests` — DPAPI round-trip, overwrite, delete isolation
- `ThemeManagerTests` — WPF resource application (STA thread), lazy theme load
- `ThemeSettingsTests` — hex color parsing, JSON round-trip, metadata coverage
- `DisplayDeviceEngineTests` — touch events, protocol constants, dispose safety
- `PriceFeedManagerLifecycleTests` — subscription/unsubscription lifecycle and GetPrice seam behavior

## Architecture Decisions

| ADR | Decision | Rationale |
|-----|----------|-----------|
| [ADR-0001](docs/adr/0001-synchronous-transport-interface.md) | Synchronous transport interface | USB I/O is inherently blocking; fake async adds cognitive overhead and prevents compile-time detection of sync-over-async |
| [ADR-0004](docs/adr/0004-librehardwareservice-shared-memory-for-sensors.md) | LibreHardwareService shared memory as hardware sensor source | Replaces the in-house WCF sensor reader; LHS's LocalSystem service + mutex-guarded shared-memory maps match the non-elevation and low-overhead goals with no custom hardware-polling code to maintain |
