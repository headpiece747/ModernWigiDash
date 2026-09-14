# Vendor reads are served from a background-refreshed cache

## Status

Accepted (2026-09-14)

## Context

The HWiNFO widget read the vendor WCF service synchronously from the render
tick: `ISensorValues.GetSensorList` / `GetSensorValue` took the client lock and
performed a blocking SOAP call on the WPF dispatcher (the list every 5 s, the
selected value every 500 ms). The service is local, so a healthy call is fast,
but a present-but-stalled service blocks the dispatcher for the binding
timeouts (open 5 s, send/receive 10 s), and `ReceiveTimeout` is an idle timeout,
so a peer trickling bytes can hold the read open indefinitely. The compositor's
render path has no catch above it, so the freeze is the whole app.

The vendor service is the same shape as the other external telemetry sources
(LibreHardwareService, PresentMon): a local, pollable, staleness-sensitive
source. Those are read through a producer on a background loop plus a static
store, so the UI never performs their I/O (ADR-0011 image).

There is exactly one `WigiDashServiceClient` per process, shared through
`VendorService.Instance`, so a cache in the client already serves every widget
instance; the producer-plus-store machinery (a store, a poll loop, and per-key
subscription bookkeeping) would add surface for the same property.

## Decision

- `GetSensorList` and `GetSensorValue` are served from an immutable snapshot
  held by the client. A read that finds a missing or stale entry schedules a
  background refresh and returns the current snapshot (null before the first
  one), so the UI thread never performs WCF I/O.
- The refresh runs on the thread pool, single-flight and rate-limited (one
  attempt per second); the cache time-to-live is 2 s for the sensor list and
  500 ms for a reading. The HWiNFO widget is unchanged: it already treats null
  as "no data" and draws its placeholder.
- Connection state (the channel, the provider, the reconnect/retry machine)
  still mutates under `_gate`, the lock the synchronous `TryConnect` path uses.
  Only the published snapshot swaps under a separate `_cacheLock`, so a reader
  never waits on an in-flight I/O. `ISensorValues.IsReady` is a lock-free
  volatile flag.
- A failed refresh clears the cached readings (the widget degrades to its
  placeholder instead of showing a stale value) and its time-to-live stamps
  back the next attempt off, so a wedged service costs one pool-thread attempt
  per rate-limit window, not a UI block.

## Consequences

- Reads are eventually consistent, bounded by the cache time-to-live. The first
  read after startup returns null for at most one refresh; the widget shows its
  placeholder for that frame.
- One client serves every HWiNFO widget, so N widgets no longer mean N
  independent 673-item list reads.
- A stalled vendor service can no longer freeze the UI. The cost moves to one
  thread-pool thread blocked for the binding timeout, at most once per second.
- The facet tests await the background refresh (`TestWait`) instead of asserting
  synchronously; the channel-factory and clock seams make the whole
  open -> fail -> drop -> reopen sequence drivable without the service.
