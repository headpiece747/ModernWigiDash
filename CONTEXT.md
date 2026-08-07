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
| **Render tick** | The 30 FPS `DispatcherTimer` in MainWindow that calls `Compositor.Compose()`, converts the frame to RGB565, and queues it for delivery. |

### Hardware / Transport

| Term | Definition |
|------|-----------|
| **Transport** | The USB HID communication layer. `DisplayHidTransport` handles WinUSB (primary) and LibUsbDotNet (fallback) for control transfers, bulk writes, and touch reads. All operations are synchronous — no fake async wrappers. |
| **FrameEncoder** | Converts SKBitmap (RGBA) → RGB565 little-endian byte array for the display's framebuffer format. Handles scaling when the source bitmap isn't exactly 1016×592. |
| **TouchReport** | Hardware touch input from the display: type (Down/Up), X/Y coordinates. Polled by the worker and routed through a channel to MainWindow for widget delivery. |
| **DisplayProtocolConstants** | USB vendor commands, framebuffer dimensions, and protocol constants for the WigiDash hardware. |
| **WinUsbBulkDevice** | Raw WinUSB P/Invoke wrapper for bulk and control transfers. Owned lifecycle (opens SetupAPI handle, initializes WinUSB, manages pipe timeouts). |

### Service Architecture

| Term | Definition |
|------|-----------|
| **Service** | A Windows Service (`ModernWigiDashService`) running as LocalSystem that owns the USB device, captures telemetry, and exposes a WCF endpoint. |
| **WCF endpoint** | `net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc` — the CoreWCF service contract for IPC between the service and the app, hosted over a named pipe (kernel-level ACL security; no TCP exposure). |
| **Service.Contracts** | Shared assembly containing the WCF contract interface (`IModernWigiDashDisplayServiceContract`), DTOs (`FrameTimeSnapshotDto`, `SensorSnapshotDto`), data models, and the WCF client (`ModernWigiDashDisplayServiceClient`). No Hardware dependency — purely a contract library. |
| **DetectServicePort** | Protocol-verified service discovery: probes known named pipe endpoints → WCF GetVersion handshake. An impostor pipe cannot hijack frames without speaking the contract. |
| **Standby** | The display's idle state: the built-in vendor Welcome screen. Entered on every owner-exit path — app close (via the WCF Shutdown op, or the engine's Dispose in direct-USB mode) and service stop (`DisplayHardwareWorkerService.StopAsync`). After standby, heartbeats stop, so the display also sleeps on its own timeout. |

### Telemetry & Data Flow

| Term | Definition |
|------|-----------|
| **LhmSensorStore** | Static in-process cache of hardware sensor readings from LibreHardwareMonitor. Written by MainWindow's sensor polling loop; read by `HardwareMonitorWidget`. `LastUpdate` timestamp enables staleness detection. |
| **FrameTimeStore** | Static in-process cache of FPS/frame-time snapshots from the ETW reader. Written by MainWindow's frame-time polling loop; read by `FrameTimeWidget`. Same staleness semantics. |
| **FrameTimeReader** | Background service that captures ETW present events (DXGI/D3D9/DxgKrnl) and builds `FrameTimeSnapshotDto` records. |
| **LhmSensorReader** | Background service that polls LibreHardwareMonitor for hardware readings (CPU/GPU temps, fan speeds, etc.) and builds `SensorSnapshotDto`. |
| **UpdateFromDto** | Maps WCF DTOs to widget-side records on the store. Centralizes the DTO→render-model translation in the store layer. |

### Widgets

| Term | Definition |
|------|-----------|
| **Widget metadata** | `[WidgetMetadataAttribute]` defines the plugin ID, display name, category, and default grid size. Read by `WidgetPluginLoader` for catalog registration. |
| **Widget property** | `[WidgetPropertyAttribute]` on a widget's public property defines the inspector UI (text/number/boolean/color/choice). Widgets implement `IWidgetPropertyOptionsProvider` for dynamic choice lists. |
| **Widget action** | `IWidgetActionInvoker` / `IWidgetActionPresentationProvider` — widgets that expose executable actions (e.g., Twitch login, hotkey execution). |
| **Inspector panel** | The right-side settings panel in MainWindow; populates UI from `[WidgetProperty]` attributes and calls `ApplyInspectorPropertyValue` to write values back. |
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
| **Hardware touch** | Physical touch from the display, polled by DisplayHardwareWorkerService, routed through a `Channel<DisplayTouchInput>` to MainWindow. |
| **Widget touch** | Local-coordinate touch events delivered to `IModernWidget.OnTouch()` after compositor hit-testing. |
| **Gesture** | Swipe (70px threshold) for page navigation, arrow-tap (60/964 edge zones) for page switch, widget tap for selection. Applied in both USB-direct and WCF paths. |

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
2. **Touch flow**: `DisplayHardwareWorkerService.RunTouchPollLoop()` → `Channel<DisplayTouchInput>` → MainWindow → `SkiaFrameCompositor.RouteTouch()` → `IModernWidget.OnTouch()`
3. **Sensor flow**: `LhmSensorReader` (BackgroundService) → `LhmSensorStore.Update()` (static cache) → `HardwareMonitorWidget.Render()` reads snapshot
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

- 134 unit tests covering protocol framing, RGB565 encoding, DTO mapping, telemetry store freshness, WCF contract consistency, touch routing, widget property defaults, and price-feed lifecycle
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
