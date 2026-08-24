# ADR-0017: The telemetry widgets degrade gracefully when the external services are absent

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

The sensor data and the frame-time data come from two external LocalSystem
services: LibreHardwareService, which publishes sensor readings to named
shared-memory maps (ADR-0004), and the PresentMon Service, which the app
connects to through its client protocol (ADR-0003). The app does not install,
own, or update either service, and it deliberately ships no copy of the
PresentMon client binaries (the client-service protocol is not
backward-guaranteed, issue #383).

## Decision

An absent service is a display state, not an error state. The producers (the
App's `TelemetryProducers`) keep polling on the `PollLoop` shape with
error-dedup logging (one line per changed error, not per tick), and the
widgets render a named placeholder: "LibreHardwareService not running" on
the hardware monitor, "PresentMon not installed" on the frame-time widget.
There is no in-process fallback source: the in-house ETW reader and the
in-house sensor polling were retired by ADR-0003 and ADR-0004 precisely to
stop maintaining them, and a bundled fallback would resurrect that
maintenance behind the same store seam.

## Exit plan

Bundle a fallback source behind the same store seam (the App's
`TelemetryProducers` already pick the producers; a fallback producer is one
more instance with the same graceful-stop behavior) only when the placeholder
becomes the user-visible complaint. The store seam (`UpdateFromDto` writes,
`TryReadFresh` reads) does not change, so a fallback is an App-side addition,
not a Widgets change.

Trigger conditions:

1. On-device validation or a user report finds a flow that depends on
   telemetry without the services (the placeholder stops being graceful and
   becomes a dead end).
2. A decision to bundle a sensor or frame-time source (supersedes the
   ADR-0004 rationale and this ADR's no-fallback clause, and is recorded as
   a new ADR).

## Consequences

**Positive:**

- The app runs correctly with no external services installed. The widgets
  render a named state instead of an empty rectangle, and the rest of the
  display is unaffected.
- No maintenance surface for a fallback pipeline: no bundled binaries, no
  in-house ETW capture, no second sensor poller.
- The graceful stop is observable (a deduped log line per changed error) and
  cannot wedge the poll loops.

**Negative (the debt this ADR registers):**

- On a machine without the services, the telemetry widgets are permanently
  decorative: the placeholder is a terminal state, not a retrying one.
- The absence verdict comes from the producer's reads, not from a service
  probe: a service that dies after its first readings keeps the last
  snapshot serving until the store's staleness window expires, and only then
  does the widget fall back to the placeholder. A dead service and an absent
  service converge on the same display state.

## Date

2026-08-24