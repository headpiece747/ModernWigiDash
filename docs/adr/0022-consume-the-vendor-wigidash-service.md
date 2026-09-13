# Consume the vendor WigiDash service

## Status

Accepted (2026-09-13)

## Context

The vendor's WigiDash Manager ships a local Windows service (`WigiDashService`) that
exposes a BasicHttp WCF endpoint at `http://localhost:8733/WigiDashService/WigiDashWcf/`
with no authentication on the binding. The service owns two hardware integrations we
want to surface as widgets:

1. **HWiNFO sensor readings** via `GetSensorList()` / `GetSensorValue(readingType,
   sensorId1, sensorId2)` returning `(double value, bool isValid)`.
2. **AIDA64 panel image frames** via `ReadAidaMmap(offset, length)` returning
   `(bool ok, byte[] buffer)`, where the bytes are the rendered AIDA64 sensor panel
   (pixel layout to be probed live; expected 1016×592×2 = RGB565).

We do not fork or bundle the vendor service. We consume it over its existing contract.
The service is absent on machines without the vendor Manager installed; our widgets
degrade to the house placeholder (ADR-0017 image) when the connection fails.

## Decision

- One `WigiDashServiceClient` connection lifecycle with two narrow read facets
  (`ISensorValues`, `IAidaPanel`) mirroring the vendor's own split. The client owns
  the WCF channel open/close/reconnect; the facets are stateless reads over the
  established channel.
- Tolerant parsing: a malformed or partial response degrades to a named "no data"
  verdict, never a throw into the render tick.
- No god-client: the client exposes only the two facets plus a connection-state
  probe. Adding a third facet requires a new narrow interface, not a method dump.
- Testable without the service: an in-memory fake implements both facets behind the
  same seam, so widget tests drive the full read path without a network call.

## Consequences

- A machine without the vendor Manager sees placeholders for both widgets (the
  ADR-0017 pattern), not errors.
- The WCF channel is a long-lived resource owned by the client; dispose must close
  it cleanly (the house handle-ownership pin applies).
- Pixel layout of the AIDA64 frame is an empirical fact to be resolved during build
  (probe `ReadAidaMmap` byte count against the known framebuffer geometry).
