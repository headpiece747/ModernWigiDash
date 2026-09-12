# ADR-0003: PresentMon Service as Frame-Time Source

**Date:** 2026-08-08  
**Status:** Accepted (2026-08-30: the `ModernWigiDashService` "isolated, preserved in the repo" notes below are stale — ADR-0005 removed the Service entirely; the PresentMon decision itself is unchanged)  
**Deciders:** Project owner

## Context

The frame-time pipeline (`FrameTimeReader` → `FrameTimeStore` → `FrameTimeWidget`) captures ETW present events from inside the Windows Service. That captures hardware timings (FPS, frame times, GPU busy) for the target process, but it carries two problems:

1. **Elevation surface**: The in-house ETW reader runs inside the service (LocalSystem). Any widget that depends on it inherits the "service must be running" coupling, and the reader itself is custom code that has to stay correct against ETW provider contracts (DXGI/D3D9/DxgKrnl).
2. **Dupes a solved problem**: PresentMon (Intel) already implements exactly this capture, a Windows service that owns the ETW session, plus an API (`PresentMonAPI2.dll`) that clients load at runtime and query over a named pipe.

Separately, the project's own `ModernWigiDashService` had three historical roles: USB transport (the app has a working direct-USB engine), LHM sensor capture (later externalized to LibreHardwareService shared memory, ADR-0004), and frame-time ETW capture (this ADR). With all three externalized, the service has no active role; it was removed wholesale by ADR-0005 (the "isolated, preserved in the repo" wording that followed this paragraph was a transitional state, since superseded).

## Decision

**Replace the in-house ETW frame-time capture with Intel's PresentMon Service** as the sole frame-time source.

- **Delete** `FrameTimeReader` and the ETW present-event capture path.
- **Keep** `FrameTimeStore` and `FrameTimeWidget` unchanged. This is a producer swap, not a rewrite. The new producer maps PresentMon poll results into `FrameTimeSnapshotDto` and writes them through the existing `FrameTimeStore.Update` seam; the widget still reads via `TryReadFresh`.
- **Service account**: PresentMon Service registered as **LocalSystem** via `sc.exe create PresentMonService` (Intel's documented model). It owns the ETW session.
- **Client model**: the app connects **non-elevated** via `pmOpenSession(&hSession)` over the named pipe. The app **does not ship its own copy** of `PresentMonAPI2.dll`. It loads the DLL at runtime from the service SDK install dir (`Program Files\Intel\PresentMon\SDK`), pinned to the installed service version.
- **Process targeting**: PresentMon is PID-based (`pmStartTrackingProcess(hSession, pid)` must be called before queries return data). The app keeps its existing process-selection logic (preferred foreground-window PID via `GetForegroundWindow` → `GetWindowThreadProcessId`, else most-active presenter) and feeds the resolved PID to PresentMon, re-applying on target change.
- **Query model**: one dynamic query with a rolling 1s window, registering `PM_METRIC_PRESENTED_FPS` (AVG/P99/P01), `PM_METRIC_CPU_FRAME_TIME`, `PM_METRIC_GPU_TIME`, `PM_METRIC_GPU_BUSY`, `PM_METRIC_APPLICATION`. The existing 1s `PollLoop` shape polls `pmPollDynamicQuery(hQuery, pid, …)`.
- **Fallback**: when PresentMon Service is absent, the widget shows a graceful "PresentMon not installed" empty state, no crash, no admin prompt.
- **`ModernWigiDashService`**: removed wholesale by ADR-0005 (its USB role is owned directly by the app, its sensor role by LibreHardwareService shared memory per ADR-0004, and its frame-time role by PresentMon per this ADR). The earlier "isolated, code preserved" note was a transitional state.

## Consequences

**Positive:**
- Removes per-run elevation for the FPS/frametime widget. One elevated install registers PresentMon Service; the app then works non-elevated forever.
- Deletes a maintenance-heavy custom ETW capture (provider contracts, session management) in favor of Intel's maintained service.
- The widget, `FrameTimeStore`, and `PollLoop` survive unchanged. The blast radius is the producer only.
- One less service to run today (the project's own service was removed by ADR-0005; only Intel's PresentMon Service remains, and it is optional).

**Negative:**
- New runtime dependency on Intel's service + SDK install (pinned version, LocalSystem).
- Intel explicitly warns the client↔service binary protocol isn't backward-guaranteed (issue #383). The app must load the API DLL matching the installed service version, or the pin must be enforced at install time.
- PresentMon captures the process the client *tracks*; "most-active presenter" semantics now mean "the PID the selection logic last resolved", not a PresentMon-native concept.
- Portable ZIP mode can't install a service. The frame-time widget degrades to the fallback there (packaging is deferred per owner decision).

**Rationale:** Frame-time capture is exactly PresentMon's domain, and Intel's documented architecture (LocalSystem service + named-pipe client + runtime-loaded SDK DLL) matches the project's isolation and non-elevation goals with no custom ETW code to maintain.

## Alternatives considered

1. **Keep the in-house ETW reader.** Retained the elevation coupling and the custom ETW maintenance burden. Rejected: duplicates PresentMon's solved problem.
2. **PresentMon CLI capture-to-file.** The `PresentMon.exe` CLI writes CSVs/ETLs; a file watcher would add latency and a poller for what the API gives directly. Rejected.
3. **Embed PresentMonAPI2.dll with the app.** Intel's README explicitly warns binary compatibility between client and service isn't guaranteed if the client ships its own DLL. Rejected: load from the SDK dir, pinned to the service version.

## Date

2026-08-08
