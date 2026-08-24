# ADR-0011: Reflection-instantiated widgets read telemetry from static stores

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

Widgets are discovered by reflection: `WidgetPluginLoader` scans the Widgets
assembly for `[WidgetMetadata]` and constructs each widget with a
parameterless constructor. A reflection-constructed object cannot receive
injected dependencies, and the two telemetry data flows need exactly that:
the producers live in the App (`TelemetryProducers`: LibreHardwareService
shared memory, the PresentMon session) and the consumers live in the Widgets
assembly (`HardwareMonitorWidget`, `FrameTimeWidget`). The shared shape is
the Sdk DTOs (`SensorSnapshotDto`, `FrameTimeSnapshotDto`), which sit in the
lowest common layer (ADR-0005).

## Decision

The static stores (`LhmSensorStore`, `FrameTimeStore`) are the deliberate
stand-in for dependency injection. Each is a thin facade over one
`TelemetryStoreFacade<TRecord>` instance (Sdk) that owns the domain's empty
value and the staleness window: producers write through the null-tolerant
`UpdateFromDto` (producer timestamp preserved), and consumers read only
through `TryReadFresh`, so the staleness decision lives in the store and no
consumer can skip it. The static shape is a deliberate house pattern
(CONTEXT.md, Key Design Decisions).

## Exit plan

Replace the static stores with injected instances the moment widget
construction gains a dependency path: a DI container or MEF in the loader, a
factory attribute on `[WidgetMetadata]`, or a constructor parameter.
`TelemetryStoreFacade<TRecord>` is already DI-friendly (one instance per
store), so the seam shape does not change.

Trigger conditions that force the replacement:

1. A widget needs a second telemetry source with a different staleness
   window (the single global window cannot express it).
2. Two display sessions run in one process (the shared store would leak
   state between them).
3. The loader stops being parameterless-reflection, for any reason.

## Consequences

**Positive:**

- Widgets stay parameterless. The loader contract, the widget-per-file
  convention, and the `[WidgetMetadata]` surface are untouched.
- The staleness decision is declared exactly once. A consumer reading a raw
  snapshot cannot skip the freshness check.
- The DTO is the shared mailbox format between App and Widgets, with no
  shadow records.

**Negative (the debt this ADR registers):**

- Process-wide mutable global state. Widget render tests must reset the
  stores between cases (the test seam for that lives on the facade).
- Per-instance staleness is unrepresentable: one window for all consumers,
  process-lifetime.
- A second source with a different freshness cannot share the store without
  a flag, which is the trigger above.

## Date

2026-08-24