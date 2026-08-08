# ADR-0005: Remove the ModernWigiDash Windows Service

**Date:** 2026-08-08
**Status:** Accepted
**Deciders:** Project owner

## Context

The project's own Windows Service (`ModernWigiDashService`, hosting a CoreWCF
named-pipe endpoint) was **isolated** after ADR-0003 and ADR-0004: kept in the
repo but not used at runtime. Its three historical roles had each been
replaced:

1. **USB transport** — the App's direct-USB engine (`DisplayDeviceEngine` /
   `DisplayHidTransport`) owns the device; the engine polls touch at 16ms in
   direct-USB mode and normalizes it once via the shared `TouchReport.ToEventType`.
2. **Hardware sensors** — LibreHardwareService's shared-memory maps (ADR-0004).
3. **Frame-time telemetry** — PresentMon Service (ADR-0003).

At that point the Service was pure dead weight: a whole project to build and
install, a named-pipe attack surface (rate-limited but still a local DoS/impersonation
consideration), a second touch/frame path that could double-deliver, and install
machinery (`Install-ModernWigiDashService.ps1`) plus the "service locks the build
output" test-workaround note. Its only remaining consumers were its own WCF
client and the App's WCF routing state.

## Decision

**Remove the Windows Service and every trace of the WCF path.**

- Delete the `ModernWigiDash.Service` project, the `ModernWigiDash.Service.Contracts`
  project, and `Install-ModernWigiDashService.ps1` from the repository and the solution.
- Delete the WCF surface: `IModernWigiDashDisplayServiceContract`,
  `ModernWigiDashDisplayServiceClient`, `ServiceUnavailableException`,
  `DisplayStatus`, `ServiceDiagnostics`, `FramePayload`, `TouchEventInfo`, and the
  App's routing machinery (`ServiceRoutingState`, `FrameSinkRouter`, the WCF-gated
  touch poll, `InitializeWcfRoutingAsync`/`ServiceReady`/`TryRetryServiceRouting`).
- **Move the live telemetry DTOs** (`SensorSnapshotDto`, `SensorReadingDto`,
  `FrameTimeSnapshotDto`) into `ModernWigiDash.Sdk` as plain data models (no
  DataContract attributes) — they remain the mailbox format shared by the App's
  producers and the Widgets' stores.
- The App now binds one `FrameDelivery` to the direct-USB engine and sends frames
  straight through it; the engine always connects directly (no service-yield check).
- Drop the WCF/service packages (CoreWCF.*, System.ServiceModel.NetNamedPipe,
  Microsoft.Extensions.Hosting.WindowsServices, System.ServiceProcess.ServiceController)
  and the WCF/service tests (`WcfDisplayServiceTests`, `WcfClientServerConsistencyTests`,
  `ServiceHostSmokeTests`, `ServiceContractTests`, `ServiceRoutingTests`,
  `FrameSinkRouterTests`, `FrameSinkIntegrationTests`).
- Update CONTEXT.md and README to the service-less architecture.

## Consequences

**Positive:**
- Removes a whole project, its install script, and the named-pipe attack surface.
- No dual-path frame/touch logic; one transport, one touch loop, one delivery policy.
- Simpler mental model: the app talks to the display directly, and external
  services (LibreHardwareService, PresentMon) are optional data sources.
- Plain builds/tests no longer collide with a running service's output locks.

**Negative:**
- The vendor's "service keeps streaming while the UI is closed" property is gone
  (the app and the display share one process lifetime).
- The `Service.Contracts` name is retired; DTOs now live in Sdk (the lowest layer).
- Loses the WCF channel's built-in versioned-contract discipline for the DTOs
  (they are now plain data models; producers/stores compile against the same assembly).

**Rationale:** Once telemetry came from PresentMon/LibreHardwareService and the app
owned the USB device directly, the Service and its WCF pipe were dead weight — a
whole project, a named-pipe attack surface, and install machinery to maintain.

## Alternatives considered

1. **Keep the Service "isolated" indefinitely** — it was already unused; retaining
   it cost build time, install docs, and a second input path that could regress.
2. **Keep `Service.Contracts` as a DTO-only assembly** — the assembly name and
   namespace ("Service") would be misleading with no service in the repo; Sdk is
   the natural lowest-common-layer home.
3. **Keep the WCF touch poll as a fallback** — the engine now polls touch in
   direct-USB mode; there is no second source to fall back to.

## Date

2026-08-08
