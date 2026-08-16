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
| **FrameDelivery** | The single frame-delivery policy module (Sdk): bounded DropOldest channel → drain-to-latest → paced send, owning encode, an exact-size buffer pool sized from the encoder's output (self-sized by construction — a pool that disagrees with the encoder is unrepresentable), and drop accounting. One entry point feeds the policy: `Push(SKBitmap)` (encode + pool); a `FrameDelivery.Create` factory makes the required encode/pool/send seams unrepresentable at production bind sites (the ctor remains for tests exercising unconfigured readiness). The App's direct-USB sink is its one runtime instance; pacing defaults to 33ms, the USB engine's device-capability rate. Drop counting lives inside the module. |
| **DisplayGeometry** | The WigiDash framebuffer geometry (Sdk) — the single source of truth for the active pixel area (1016×592, 2 bytes/pixel, payload size). Hardware's `DisplayProtocolConstants` aliases these values and Core's compositor derives its buffer from them, so the pixel area can never drift between projects. |
| **IRgb565Encoder / SkiaRgb565Encoder** | The encode seam (Sdk/Hardware): `IRgb565Encoder` exposes `OutputBufferSize` + `Encode(SKBitmap, byte[])` — the delivery pipeline encodes through it, and its output size sizes the delivery's buffer pool; `SkiaRgb565Encoder` is the production adapter over `FrameEncoder`. Tests inject scriptable fakes, so frame delivery is testable without pixels. |
| **ConnectProvider** | One connect attempt in `DisplayHidTransport.Connect`'s provider loop (Hardware): `TryCreate` opens the device and returns the backend (or null when that driver stack cannot provide one), plus the diagnostic message fields that keep each leg's log lines byte-identical after the loop refactor. The default list holds the WinUSB provider first and the LibUsbDotNet fallback second; tests inject fakes through the single `ProviderFactories` seam — including a fake LibUsb leg — to drive the connect fallback policy without hardware. The LibUsb leg's device lookup is itself injectable (`LibUsbDeviceProvider`), so its open/config/claim/endpoint teardown is scriptable; every leg failure disposes the LOCAL device, never the adopted global backend (the orphan rule). |
| **WinUsbApi** | The WinUSB/SetupAPI P/Invoke surface as an injectable delegate bag (Hardware, the PresentMonNative precedent): production binds the real externs once via `WinUsbApi.Default`; tests inject managed fakes, so `WinUsbBulkDevice.Open`'s failure and cleanup paths are scriptable without hardware. |
| **Render tick** | The 30 FPS `DispatcherTimer` in MainWindow that calls `Compositor.Compose()`, converts the frame to RGB565, and queues it for delivery. |

### Hardware / Transport

| Term | Definition |
|------|-----------|
| **Transport** | The USB HID communication layer. `DisplayHidTransport` picks one **ITransferBackend** in `Connect` through the **ConnectProvider** loop — WinUSB (primary) or LibUsbDotNet (fallback) — and runs its connect/init/frame/touch policy against that seam; the backends own the device-specific transfers and teardown (WinUsbBulkDevice implements the seam directly, LibUsbTransferBackend wraps the chunked-write path). `IDisplayTransport` exposes only the live surface (Connect, SendFrame, ReadTouch, IsConnected, GoToStandby). All operations are synchronous — no fake async wrappers. |
| **ITransferBackend** | The transport's USB transfer seam (Hardware): vendor control in/out + bulk write. Two real adapters (WinUSB, LibUsbDotNet) plus test fakes, so the transport's policy is drivable without hardware and the backend choice is made once in `Connect` instead of re-decided per call. |
| **ConnectionState** | The engine's single connection truth (Hardware): `Disconnected` / `Connecting` / `Connected` / `Simulated` — one value instead of the old lockstep IsConnected/IsHardwareActive/IsSimulationMode trio callers disagreed on. The presenter gate and the USB badge read the same state. |
| **DisplayDeviceEngine** | The App's hardware engine (Hardware): owns transport connection, standby, and the 16ms touch poll. The constructor is deliberately inert — no connect attempt, no background loops (construction never reaches for hardware, so test hosts are safe); `Start()` begins the initial connect, the 5s reconnect timer, and touch polling. The connect machine is drivable end-to-end through the transport factory (the internal test ctor wires the factory to the injected transport, so even a Start()-triggered reconnect routes through it); the touch loop's failure observer logs instead of going silent. |
| **FramePump** | The 30 FPS presentation cadence module (App): dispatcher timer that composes the active page, hands the frame to the presenter, and requests a repaint — the window keeps only the WPF draw, so the buffer drawn is exactly the buffer sent. Badge refreshes ride the tick as an injected callback. |
| **FrameEncoder** | Converts SKBitmap (RGBA) → RGB565 little-endian byte array for the display's framebuffer format (BGRA/RGBA fast paths + per-pixel fallback, shared `PackRgb565` bit packing; handles scaling when the source bitmap isn't exactly 1016×592). The single bit-packing owner behind the **IRgb565Encoder** seam. |
| **TouchReport** | Hardware touch input from the display: type (Down/Up), X/Y coordinates. Polled by the App's `DisplayDeviceEngine` at 16ms in direct-USB mode and normalized once via the shared `TouchReport.ToEventType` (the single mapping site, also used by the deleted Service seam). The App never sees vendor protocol bytes. |
| **DisplayProtocolConstants** | USB vendor commands, framebuffer dimensions, and protocol constants for the WigiDash hardware. |
| **WinUsbBulkDevice** | Raw WinUSB P/Invoke wrapper for bulk and control transfers. Owned lifecycle (opens SetupAPI handle, initializes WinUSB, manages pipe timeouts). |
| **TrustedUriPolicy** | The shell-open trust rule (Sdk): a host is trusted for the Twitch device-auth browser open only when it IS the twitch.tv apex or a dot-prefixed subdomain — a suffix check would admit attacker-registrable lookalikes (faketwitch.tv). One policy shared by the App's `TrustedBrowserUri` and the Widgets' auto-open guard, so the two sites can never drift. |
| **DisplayFormat** | The display-rules culture contract (Widgets): invariant, zero-aware number formatting (`Fps`/`Ms`/`Pct`/`Count`/`Number`) that every presentation module routes through — one locale contract pinned and tested once instead of per-module interpolation with the ambient culture. |
| **WeatherLocationResolver** | The weather geocoding decision policy (Widgets): candidate ranking/scoring, suffix + diacritic-insensitive matching, population tiebreak, the ambiguity gate, alias tables, and ZIP routing — the pure decision layer behind `WeatherGeocoder` (a leaf utility `WeatherClient` also calls for the forecast URL and ZIP pre-check). Every city-resolution fix commit's rule lives here with its comment. The cluster also uses `Core.Rendering.FontHelper` for the shared font cache, like every other widget. |
| **WeatherClient** | The weather fetch/cache/resolution orchestrator (Widgets): 5-minute throttle window, single-flight claim, identity gate with stale detection, the `WeatherFetchResult` outcome union, the identity-stamped atomic disk cache, and the explicit-coordinates/ZIP/city/pick resolution legs delegating decisions to `WeatherGeocoder`. The display strings never live here. |
| **WeatherGeocoder** | The geocoding transport layer (Widgets): Open-Meteo/zippopotam HTTP legs with bounded reads and per-leg deadlines, tolerant candidate parsing, and the coordinate/ZIP/city entry points — delegating every ranking/decision rule to `WeatherLocationResolver` (a leaf utility the client also calls directly for the forecast URL and ZIP pre-check). |
| **WeatherFetchResult** | The fetch-outcome union (Widgets): `Fetched`/`Throttled`/`InFlight`/`Failed`/`Stale` — a fetch result without a snapshot is unrepresentable; the widget distinguishes "try again now" (`Stale`) from "keep what you have" (the rest). |
| **WidgetRouting** | The page-input routing module (Core): hit-test + touch routing over a page's placed widgets (rotated-local coordinates, highest-ZIndex selection) — extracted from the compositor so the renderer owns only rendering + edit state. |
| **TwitchChatStatusPolicy** | The Twitch chat's connection/buffer policy (Widgets): the IRC NOTICE→connection-state transition and the message-buffer cap — split from `TwitchChatPresentation` (display strings only). |
| **WidgetCreateResult** | The widget-creation outcome (Core): `Ok(widget)` / `NotFound` / `Broken(reason)` — a broken plugin is distinguishable from an absent one (the old null-or-null ambiguity), and `PluginInfo` is an immutable record. |

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
| **LhmSensorStore** | Static in-process cache of hardware sensor readings from LibreHardwareService. A facade over `TelemetryStoreFacade<SensorSnapshotDto>` (Sdk) — the snapshot shape is the DTO itself (no shadow record); the reading label (`HardwareName: SensorName`) derives as a computed property on the DTO, the single derivation site the picker and widgets share. Written by the App's shared-memory producer (`LhmSharedMemoryReader` → `UpdateFromDto`, producer timestamp preserved); read by `HardwareMonitorWidget` through `TryReadFresh` — the store owns the staleness decision, consumers cannot skip it. |
| **ILhmMapSource** | The named-map I/O seam behind the LHS reader (Hardware/App): one mutex-guarded, bounded copy of the sensors map, or null with an error. The real adapter (`MemoryMappedLhmMapSource`) owns the MemoryMappedFile/Mutex specifics and the attacker-claimed-size copy caps; in-memory fakes drive the reader's Poll policy in tests. |
| **FrameTimeStore** | Static in-process cache of FPS/frame-time snapshots. A facade over `TelemetryStoreFacade<FrameTimeSnapshotDto>` (Sdk) with the same shape as `LhmSensorStore` — the snapshot shape is the DTO itself (no shadow record). Written by the PresentMon producer (see below) via `FrameTimeStore.UpdateFromDto` (producer timestamp preserved); read by `FrameTimeWidget` through `TryReadFresh`. The widget renders only PresentMon-reported data: an 8-metric dashboard view (FPS/frame time hero, 1% low, 0.1% low, CPU frame, GPU busy %, displayed FPS, dropped frames, GPU time, present mode) that tap-toggles to a PresentMon-overlay-style readout; both views shrink gracefully and show zero values when no process is tracked (PresentMon's own 0-presents reading; no monitor-refresh-rate display — that is not a PresentMon feature) **and when the tracked target is not actually displayed** — the producer's target-trust policy (`TargetTrustPolicy`: settling window, adoption, frozen-data guard) reports live data only for the pid that produced the last live sample: on a foreground switch it holds the zero state through a settling window (PresentMon returns the departed target's frozen data for every pid after a switch — observed on-device), an adopted target's data is only reported once it differs from the departed target's last values, and a presenting-but-not-displayed target (backgrounded fullscreen game, `PM_METRIC_DISPLAYED_FPS` = 0) also reads as the idle zero state. |
| **TelemetryStoreFacade** | The store-facade shape (Sdk): owns one `TelemetryStore<TRecord>` instance bound to the domain's empty value and staleness window, and adds the null-tolerant producer write plus the fake-clock test seams. `LhmSensorStore` and `FrameTimeStore` wrap one instance each — the staleness policy and its test surface are declared exactly once (the former `StaticTelemetryStore` pass-through layer was folded into the facade). |
| **PresentMon producer** | The frame-time source (ADR-0003). Replaces the deleted `FrameTimeReader`: the app connects to PresentMon Service (`pmOpenSession`, non-elevated), resolves a target PID (preferred foreground window, else most-active presenter), calls `pmStartTrackingProcess`, and polls a rolling 1s dynamic query (`PM_METRIC_PRESENTED_FPS` AVG/P99/P01, `CPU_FRAME_TIME`, `GPU_TIME`, `GPU_BUSY`, `DISPLAYED_FPS`, `DROPPED_FRAMES`, `PRESENT_MODE`, `APPLICATION`) on the existing 1s `PollLoop` shape. Results map into `FrameTimeSnapshotDto` → `FrameTimeStore.Update`. Loads `PresentMonAPI2.dll` from the service SDK dir at runtime — never ships its own copy (client↔service binary protocol isn't backward-guaranteed, issue #383). Absent service ⇒ widget shows a graceful "PresentMon not installed" state. |
| **PresentMonMetricCatalog** | The installed service's metric truth (App/PresentMon): built once per session from `pmGetIntrospectionRoot`, it exposes each metric's type/unit and the stats the service accepts. `PresentMonQueryBuilder` builds the dynamic query from wanted specs against the catalog — an unsupported metric degrades to a named drop (the field reads no-data) instead of an opaque QueryMalformed failure, and a rejected preferred stat falls back to the first allowed one (PRESENT_MODE only accepts NEWEST_POINT/MID_LERP). |
| **PresentMonQueryRegistry** | The query subsystem (App/PresentMon): registers the dynamic + frame queries, owns the elements, the field→element map, the blob strides, and the poll/drain loops (capacity growth, bounded drain). Pure policy over injected delegates, so the loops are unit-testable without the DLL; `PresentMonNative` keeps only the session and tracking surface. Enum-typed metrics (PRESENT_MODE) are read as 4-byte int32 per the element's DataSize, not as doubles. |
| **FrameTimeSnapshotFactory** | The only place the frame-time snapshot gets shaped (App/PresentMon): unavailable/idle/capture-dead outcomes and the live mapping with its unit conversions (1000/FPS frame time, GPU busy ms → busy-per-frame %). Extracted from the producer's poll method so the conversions are directly testable. |
| **FrameTimePresentation** | The widget's display rules (Widgets): given a snapshot and placement size, the hero strings, the eight dashboard cards, the nine overlay rows (PresentMon's metric names; 1000/percentile-fps frame times), and the shrink visibility flags. The widget's render methods are thin adapters over it — the display is assertable without pixels. |
| **TrackedTargetResolver** | The PresentMon tracking-target selection (App/PresentMon): which process to track — the preferred foreground window, else the most-active presenter — with injected foreground/children probes (real user32/toolhelp adapters, test fakes) so resolution is drivable without real processes. |
| **LibreHardwareService producer** | The hardware-sensor source (ADR-0004). Replaces the deleted `LhmSensorReader`: LibreHardwareService (LocalSystem) owns the hardware polling and publishes readings to named shared-memory maps (`sensors`, `status`; `all-hardware` optional) guarded by named mutexes. The App's `LhmSharedMemoryReader` reads via the `ILhmMapSource` seam (one mutex-guarded, bounded copy), parses the header (`MetaDataSize`, `UpdateInterval`, `LastUpdate`, `index-length/offset`, `index-format`, `data-length/offset`) and honors the declared index format (JSON or MessagePack — MessagePack-CSharp package). DataSensor records map 1:1 into `SensorSnapshotDto` (SensorId preserved; LHS publishes value/min/max only — no Avg; `UnitFor(SensorType)` replicated app-side) → `LhmSensorStore.UpdateFromDto` on the existing 1s `PollLoop` shape. Absent service ⇒ widget shows a graceful "LibreHardwareService not running" state. |
| **UpdateFromDto** | Maps sensor DTOs to widget-side records on the store. Centralizes the DTO→render-model translation in the store layer. |

### Widgets

| Term | Definition |
|------|-----------|
| **Widget metadata** | `[WidgetMetadataAttribute]` defines the plugin ID, display name, category, and default grid size. Read by `WidgetPluginLoader` for catalog registration. |
| **Widget property** | `[WidgetPropertyAttribute]` on a widget's public property defines the inspector UI (text/number/boolean/color/choice). Widgets implement `IWidgetPropertyOptionsProvider` for dynamic choice lists. |
| **Widget action** | `IWidgetActionInvoker` / `IWidgetActionPresentationProvider` — widgets that expose executable actions (e.g., Twitch login, hotkey execution). |
| **Inspector panel** | The right-side settings panel in MainWindow; populates UI from `[WidgetProperty]` attributes and calls `ApplyInspectorPropertyValue` to write values back. The reflection→editor mapping is a pure `EditorDescription` model (no WPF, no dialogs); a thin WPF mapper renders it, and write-back funnels through the single `ApplyInspectorPropertyValue` seam. Dynamic choice lists resolve through `IWidgetPropertyOptionsProvider` — no widget-specific `typeof` checks. |
| **TextRenderHelper** | Shared rendering utilities: text truncation with ellipsis, centered text drawing, title/subtitle placeholders, sparkline charts, SVG path caching. |
| **NowPlayingPresentation** | The Now Playing widget's display rules (Widgets): meta line, time format, the 11-entry friendly-app-name map, progress/seek ratios, playback-rate text — pure and assertable; the widget draws the model. |
| **NowPlayingLayout** | The Now Playing widget's touch-zone layout (Widgets): the hit regions and their precedence (shuffle/previous/play-pause/next/repeat/seek) derived from the placement size. The widget's Render draws from the same record its OnTouch hit-tests against — display and input share one geometry source. |
| **AudioFrameBuffer** | The thread-safe front of the visualizer's DSP (Widgets): owns the gate around the pure `AudioSpectrumAnalyzer` and the double-buffered output copies — the capture thread feeds sample blocks, the render thread takes one `Snapshot` per frame and draws the copy; the widget never holds the gate while drawing, and no array is allocated per frame. |
| **FeedSubscription** | One ticker widget's feed-identity ownership (Widgets): diffs the tracked identity against the last one, releases the old claim, subscribes the new one, and seeds the fallback fetch — the subscription bookkeeping is testable without a widget instance. |
| **SvgIconHelper** | Shared parsed-SVG-path helpers (Widgets): the draw-scaling protocol and the parse cache used by the bundled Griddy icon set and runtime-loaded icon files. Split out of TextRenderHelper so the text/sparkline helpers stay free of icon machinery. |
| **WeatherPresentation** | The Weather widget's display rules (Widgets): WMO code table, unit-system parsing, temperature/speed formatting, and the per-mode pill/row strings. The format helpers live here (not in the data module); `WeatherClient` stays fetch/cache/resolution — the display strings never live there. |
| **WeatherRenderModel** | The cached render model (Widgets): every formatted string the five layout modes draw plus the data slices, keyed by data version + bounds + property snapshot — recomputed only when the key components change. |
| **WeatherWidgetRenderer** | The widget's draw paths (Widgets): the five layout-mode render methods over a `WeatherRenderModel`, thin adapters over `WeatherPresentation`'s display facts and `WeatherLayout`'s geometry — the only consumer is `WeatherForecastWidget`. |
| **TwitchChatPresentation** | The Twitch chat widget's display rules (Widgets): the header status line and the empty-state hint per `ChatStatus` — pure and assertable. |
| **PriceFeedMessages** | The price-feed wire-format parsers (Widgets): Binance WS/REST, Finnhub WS/REST, CoinGecko simple-price. Pure and directly tested; PriceFeedManager's handlers and poll bodies are thin write adapters over them. |
| **UsbBadgeModel / CatalogFilter** | The window's last stateful display logic (App), extracted: the USB engine state → (label, brush) mapping and the widget-catalog filter/sort — pure and assertable without WPF. |
| **PageTabsView / PageTabVisual** | The page-tabs strip module (App): owns tab construction, the wheel scroll, and scroll-into-view; the window keeps only the switch/rename/delete page seams. The pure per-tab geometry rules (`PageTabVisual`: padding/margins derived from active and delete state) have no UI tree — the constants are pinned by tests. |
| **ClockPresentation / StopwatchPresentation / TickerPresentation** | The clock, stopwatch, and ticker widgets' display rules (Widgets): AM/PM + date strings, mm:ss.cc elapsed + status text, the price-decimal tier rule + label fallback — pure and assertable. |
| **ChunkedBulkWrite** | The LibUsb chunked bulk-write policy (Hardware): bounded chunks sized for the legacy libusb driver, advancing by the actually-transferred length so short writes never skip a gap. Pure over a write delegate — the only formerly zero-test module in the repo is now fully covered. |
| **PriceFeedManager** | Shared WebSocket-based price streaming for stock/crypto/FX tickers. One crypto symbol table (alias → base coin + CoinGecko id) behind an `IWebSocketFeed` seam — Binance/Finnhub feed adapters plus in-memory fakes in tests; Yahoo/CoinGecko REST fallbacks share the process-wide HttpClient, which the manager never owns. |
| **IWebSocketFeed** | The WebSocket seam (Widgets) behind the price feeds and the Twitch IRC loop: connect, send text, receive one complete text message per await, abort. `ClientWebSocketFeed` is the production adapter; in-memory fakes drive both consumers' loops in tests — one framing implementation per assembly. |
| **IWidgetIconGrab** | Widget contract for edit-mode icon dragging (Core): the widget owns its icon geometry — hit region, center, and grab-move math — so the input module sees only the capability, never the widget type. Mirrors the `ResizeHandleSize` ownership pattern. Hit-testing runs in the widget's rotated-local space (`PlacedWidgetInstance.ToLocalPoint` — the render-transform inverse), so a rotated icon's grab region matches its drawn footprint. Offsets persist through `ModernWidgetBase.SetProperty`. |
| **TelemetryProducers** | The telemetry producer cluster (App): owns the two direct poll loops (sensor 1s via LibreHardwareService shared memory, frame-time 1s via PresentMon), their producers/readers, and the error-dedup state. The window keeps one `Start()`/`Dispose()`; the cluster is testable without WPF. |
| **LogCadence** | The diagnostic log-cadence rule (Sdk): fires on the first call and/or every Nth — one tested rule replaces the hand-rolled modulo counters that once mirrored each other across the transport, the backend, and the delivery pipeline. |
| **DiagLog** | The category-tagged diagnostic log line (Sdk): composes `LogCadence` (first-log/every-Nth) with `FileLog` and bakes the caller's tag in once at construction — the tag can never drift between call sites. |
| **SetProperty / PersistProperty** | The single widget-property bookkeeping invariant (Sdk/App): a widget property lives in both the instance property and the placed instance's `PropertyValues` (the export format). `ModernWidgetBase.SetProperty` writes instance + `OnPropertyChanged` + persistence; the App's context (`PersistProperty`) resolves the owning placed instance by identity. Every mutation path — inspector write-back, icon-grab moves, widget `OnTouch` toggles — routes through it, so export round-trips cannot silently lose runtime toggles. |
| **FeedLoop** | One WebSocket reconnect-loop shape (Widgets), in the `PollLoop` image: create feed → connect → onConnected → read messages until closed → backoff delay, repeating until cancelled. PriceFeedManager's Binance/Finnhub loops and the Twitch IRC loop are instances with injected `IReconnectPolicy` (fixed vs exponential backoff) and status/error hooks. |
| **IAudioCaptureSource** | The audio-capture seam behind the visualizer (Widgets): a source of float samples from the system output, drivable by an in-memory fake in tests. `WasapiLoopbackCaptureSource` is the NAudio adapter; the buffer→float conversion is the pure `AudioSampleConverter` (IEEE-float 32, PCM 16/24/32-bit with 24-bit sign extension — pinned by tests); `AudioSpectrumAnalyzer` (pure binning/smoothing) and the gate live behind the thread-safe **AudioFrameBuffer**. The widget renders snapshots; it never touches WASAPI. |

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

- 1240 unit tests covering protocol framing, RGB565 encoding, DTO mapping, telemetry store freshness, touch routing/normalization, frame delivery/coalescing, widget property defaults + persistence round-trips, price-feed lifecycle and WebSocket-seam behavior, the REST poll bodies (Finnhub/Frankfurter/CoinGecko) through the HttpClient seam, transport policy through the ITransferBackend seam (including the connect fallback policy and standby), FramePump cadence, IRC-loop behavior through the feed seam, the Twitch device-token poll loop (interval growth, slow_down, timeout/error expiry) through the HttpClient/Clock seams, audio DSP + capture lifecycle, icon-grab geometry, SVG-path draw scaling + cache fallback/keying (`SvgIconHelper`), page-tab rules, the tab-strip seams, wheel-scroll inversion and scroll-into-view (`PageTabsView`), telemetry-producer ticks, cached color parsing, stopwatch timing, feed subscription lifecycle, log-cadence and log-on-change rules, dialog hosts, inspector write-back, profile-import sanitization caps (including null collections), present-mon blob parsing, the input press orchestration (source-aware Press/Move/Release), the update flow (checker/service/script through their seams), the teardown standby guarantee (`ShutdownOrchestrator`), the widget memo pattern (`MemoSlot`), the fallback-seed cadence (`TickerFallbackPolicy`), the display-rules culture contract (`DisplayFormat`), the shell-open trust policy (`TrustedUriPolicy`), the widget-creation outcome policy (`WidgetCreateResult`), the routing module (`WidgetRouting`), the IRC status policy (`TwitchChatStatusPolicy`), the location-resolution rules (`WeatherLocationResolverTests`, `TrustedUriPolicyTests`, `DisplayFormatTests`), the weather fetch-outcome union (`WeatherFetchResult`), and the weather/twitch/theme layout & message modules (`WeatherGeocoder`, `WeatherLocationResolver`, `WeatherLayout`, `WeatherWidgetRenderer`, `TwitchIrcMessages`, `SymbolCatalog`, `ThemeApplicator`, `WrapCache`)
- `DisplayProtocolTests` — widget config layout + RGB565 encoding (BGRA framebuffer format)
- `DisplayDeviceEngineTests` — direct-USB touch polling (normalized events, null-report skip), touch type normalization, protocol constants, dispose safety
- `LhmSharedMemoryReaderTests` — LHS map parsing (JSON + MessagePack index), unit table, malformed-input fallbacks
- `TelemetryStoreMappingTests` — DTO→store mapping, freshness tracking, null-DTO handling
- `TwitchTokenStoreTests` — DPAPI round-trip, overwrite, delete isolation
- `ThemeManagerTests` — WPF resource application (STA thread), lazy theme load
- `ThemeSettingsTests` — hex color parsing, JSON round-trip, metadata coverage
- `PriceFeedManagerLifecycleTests` — subscription/unsubscription lifecycle and GetPrice seam behavior
- `PriceFeedSocketLoopTests` — WebSocket-seam loop behavior (ticker apply, reconnect) through the feed seam (the CoinGecko table invariant moved to `SymbolCatalogTests`)
- `DisplayHidTransportTests` — transport policy (init sequence, frame framing, touch parsing) through the ITransferBackend seam
- `FramePumpTests` — 30 FPS cadence wiring on a live STA/Dispatcher
- `TwitchChatStreamLoopTests` — IRC loop behavior (handshake, reconnect backoff, PRIVMSG parsing) through the feed seam
- `FontAndTextTests` / `GriddyIconsTests` / `HotkeyActionTests` — subject files own their tests (widget tests live with their widgets); the old UnitTestSuite grab-bag is retired and its residual groups were folded into their subject files
- `NowPlayingLayoutTests` / `AudioFrameBufferTests` / `FeedSubscriptionTests` — the widget module seams (touch-zone precedence, the buffer gate + double-buffer, feed-identity bookkeeping) pinned without a widget instance
- `PageTabVisualTests` — the per-tab geometry rules (padding/margins from active + delete state) with no UI tree
- `PageTabsViewTests` — the tab-strip module through its real Panel/ScrollViewer on STA: tab/rename/delete seams fire with the right index, the close button obeys the delete rule, the wheel maps to an inverted horizontal offset, and ScrollToPage brings the tab into view
- `SvgIconHelperTests` — the draw-scaling protocol pinned on pixels (placement, scale, offsets, no-op guards) and the parse cache (empty-path fallback, case-insensitive keying, parse-once)
- `DiagLogTests` — the category-tagged log line's composition rule (first-log/every-Nth + tag) through the injected write seam
- `WinUsbBulkDeviceTests` — the WinUSB/SetupAPI open and cleanup paths through the delegate-bag seam (`WinUsbApi`)
- `SymbolCatalogTests` — symbol/FX validation and normalization, alias resolution, asset-kind mapping, and the single crypto table's CoinGecko invariant (moved out of the feed-lifecycle tests)
- `TwitchChatPresentationTests` — the header status line, empty-state hint, and status color per `ChatStatus`
- Shared test doubles in `TestDoubles.cs` — one `TestContext` / `FakeFeed` (queued messages, parkable connect) / `StubHttpHandler` (delegate, canned-body, queue, and gate modes) / scriptable `StubPresentMonNative` / `StubMediaSessionSource` triple / scriptable `StubLhmMapSource` / `RecordingBackend` (the transport-policy seam) / `StaHost` (App-hosting STA pump) / `StaRunner` (one-shot STA invoke, no App host) / `ResetApplicationState` (clears the shutdown flag a closed window's Application leaves set, which would silently disable later `Window.Show`s) per seam instead of a per-file copy, plus `FakeTimeProvider` (Microsoft.Extensions.Time.Testing) for clock-dependent tests

## Architecture Decisions

| ADR | Decision | Rationale |
|-----|----------|-----------|
| [ADR-0001](docs/adr/0001-synchronous-transport-interface.md) | Synchronous transport interface | USB I/O is inherently blocking; fake async adds cognitive overhead and prevents compile-time detection of sync-over-async |
| [ADR-0002](docs/adr/0002-named-pipe-wcf-transport.md) | ~~Named-pipe WCF transport~~ **Superseded by ADR-0005** | The WCF pipe is gone with the Service (ADR-0005); kept for history |
| [ADR-0003](docs/adr/0003-presentmon-service-for-frame-time.md) | PresentMon Service as frame-time source | Replaces the in-house ETW reader; Intel's LocalSystem service + named-pipe client matches the non-elevation goal with no custom ETW capture to maintain |
| [ADR-0004](docs/adr/0004-librehardwareservice-shared-memory-for-sensors.md) | LibreHardwareService shared memory as hardware sensor source | Replaces the in-house WCF sensor reader; LHS's LocalSystem service + mutex-guarded shared-memory maps match the non-elevation and low-overhead goals with no custom hardware-polling code to maintain |
| [ADR-0005](docs/adr/0005-remove-windows-service.md) | Remove the ModernWigiDash Windows Service | Once telemetry came from PresentMon/LibreHardwareService and the app owned the USB device directly, the Service and its WCF pipe were dead weight — a whole project, a named-pipe attack surface, and install machinery to maintain |
