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
2. **AIDA64 panel image frames** via `ReadAidaMmap(offset, length)`. This facet
   was superseded the same day: the WCF round trip base64-encoded ~1.2 MB per
   frame, so the AIDA64 widget reads the vendor's shared map directly instead
   (ADR-0024). `IWigiDashWcf` no longer mirrors the AIDA operations.

We do not fork or bundle the vendor service. We consume it over its existing contract.
The service is absent on machines without the vendor Manager installed; our widgets
degrade to the house placeholder (ADR-0017 image) when the connection fails.

## Decision

- One `WigiDashServiceClient` connection lifecycle with one narrow read facet
  (`ISensorValues`) mirroring the vendor's HWiNFO provider. The client owns
  the WCF channel open/close/reconnect; the facet is a stateless read over the
  established channel.
- Tolerant parsing: a malformed or partial response degrades to a named "no data"
  verdict, never a throw into the render tick.
- No god-client: the client exposes only the facet plus a connection-state
  probe. Adding another provider requires a new narrow interface, not a method dump.
- Testable without the service: an in-memory fake implements the facet behind the
  same seam, so widget tests drive the full read path without a network call.

## Consequences

- A machine without the vendor Manager sees placeholders for both widgets (the
  ADR-0017 pattern), not errors.
- The HWiNFO provider is a SHARED session on the vendor service: any consumer's
  `DeInitSensorProvider` tears it down for every other consumer, and the vendor
  Manager deinits on exit. A torn-down provider makes `GetSensorList` return an
  EMPTY list (not an error, and `GetSensorInitStatus` is not a reliable readiness
  signal: it reads 0 while the list works). The client therefore re-initializes
  the provider when a list read comes back empty (throttled to one init per 5 s)
  and deliberately does not de-initialize it on exit, so the app and the vendor
  Manager can coexist.
- The WCF channel is a long-lived resource owned by the client; dispose must close
  it cleanly (the house handle-ownership pin applies).
- WCF matches wire shape by NAME, not by position or CLR type, and a mismatch
  deserializes silently rather than faulting. Two details are load-bearing and
  pinned by `WigiDashServiceClientTests` against the live service when present:
  the sensor payload's data-contract name/namespace (`SensorItem` in
  `http://schemas.datacontract.org/2004/07/WigiDashWcf`, carried by
  `VendorSensorItem`) and each operation parameter's element name
  (`reading_type` / `sensor_id1` / `sensor_id2` / `IsValid`, carried by
  `[MessageParameter]`). A type named `VendorSensorItem` in our own namespace and
  camelCase parameters made `GetSensorList` return an empty list and every
  reading wrong, with no exception (observed on-device 2026-09-13).
