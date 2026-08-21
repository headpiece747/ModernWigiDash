# ADR-0008: The weather fetch flow's host seam is typed

**Date:** 2026-08-18
**Status:** Accepted
**Deciders:** Project owner

## Context

ADR-0007 extracted the fetch sequence into `WeatherFetchFlow` and handed the
host concerns across as a **ten-delegate bag** (a `Func` per concern, wired in
the flow's constructor). That ADR's own Negative consequence records the cost:
the seams are an *implicit* contract —

- The apply delegate took SNAPSHOT, expected-version, identity-guard,
  candidates, population, and resolved-name as SEVEN positional arguments. A
  payload field added to the request was an arity change on both sides, with
  no named spelling of the payload anywhere.
- The ten members had no shared identity: the widget spelled them as private
  methods wired by its constructor, and the test host mirrored the wiring
  field by field. "What a fetch-flow host is" had no single type to point at —
  the reader reconstructed the contract from the ctor wiring and from the
  ADR's prose.
- The gate discipline (the apply and the version read run under the host's
  gate; the write-back's check + set is one critical section) was a
  discipline kept by the mirror-image test host and by comments — not a type.

The 2026-08-18 architecture review (`docs/reports/
architecture-review-20260818-203640.html`, Candidate 1) flagged this as the
cluster's remaining shallow seam.

## Decision

**The host concerns travel across one named interface:
`ModernWigiDash.Widgets.IWeatherFetchHost`.**

- The interface spells the host contract once: `CurrentLocation` (the
  property → identity-input coercion), `DataVersion` (the display-state
  version read), `IsStaticSnapshot` (the cadence gate's veto input),
  `RunToken` (the teardown token), `TryApply(WeatherApplyRequest)` (the gated
  apply — the request in, a bool verdict out), `QueueLabelWriteback(
  Func<bool> identityGuard, string value)` (the gated write-back queue),
  `RequestRender`, and `RequestInspectorRefresh`.
- `WeatherApplyRequest` (a record: the snapshot, the expected version, the
  identity guard, the candidates, the population, the resolved name) replaces
  the apply delegate's seven positional arguments — the payload has a name,
  and a new payload field is a named addition to the record, not an arity
  change.
- The widget's EXPLICIT implementation is the production host adapter:
  `WeatherForecastWidget` implements `IWeatherFetchHost`, and the gate
  discipline is spelled once in the implementations (the `TryApply` and the
  `DataVersion` read run under the widget's `_forecastGate` — the later
  `WeatherDisplayState` extraction moved that gate into the display-state
  module the seam bodies now forward to; the write-back's check + set is one
  critical section). The flow's construction shrank to
  `new WeatherFetchFlow(_client, this)` — the later extraction removed the
  identity parameter (the inspector stamp is built from the applied payload,
  so the flow's only view of the host is the seam), leaving a primary
  constructor over (the client, the host), and the wiring bag is gone.
- The test host (`WeatherFetchFlowTests.FlowHost`) is an adapter over the SAME
  seam: the flow is constructed against the interface, so the test host and
  the production widget are two implementations of one named type — "what a
  fetch-flow host is" now has a single spelling, and a host concern added
  later is caught at both implementations at once. (The later
  `WeatherDisplayState` extraction made the mirror exact: the test host wraps
  the same display-state module the widget uses.)
- The ADR-0007 "discipline, not a type" consequence is closed for the
  structural half: the gate requirement is now the shape of an interface
  implementation the widget owns (and the test host wraps the same
  display-state module the widget uses), not an unnamed
  delegate's implicit contract. (The gate DISCIPLINE itself — that the
  implementation takes the gate — remains a host-side invariant the widget's
  live pins exercise, as before.)

## Consequences

**Positive:**
- The host contract has a name, a type, and a test-double surface; the
  flow/ host boundary is assertable by reading one interface, not a ctor.
- The apply payload is extensible without an arity change on either side.
- The flow's constructor is a primary constructor over its real
  collaborators (client, identity, host — the later extraction reduced this
  to (client, host): the identity stamp moved to the applied payload) — the
  widget's ctor wiring is one line.

**Negative:**
- One more interface in the cluster — it is the seam ADR-0007 already had,
  only typed; surface count is unchanged, identity count went from zero to
  one.

## Alternatives considered

1. **Keep the delegate bag** — the ADR-0007 state. The payload's seven
   positional arguments and the missing host identity stayed; the review
   ranked this seam the cluster's remaining shallow one.
2. **A read-only parameter object** (the reads as one record, the writes
   staying delegates) — the writes ARE the contract's heart: the gated apply
   and the gated write-back queue carry the policy (the bool verdict, the
   identity guard). Leaving them as delegates would keep exactly the
   discipline-not-type surface the ADR exists to close.
3. **Move the seam to `ModernWigiDash.Sdk`** — it is the weather cluster's
   internal module boundary; the Sdk contract would gain a dependency on the
   cluster's display-state shape for zero cross-assembly benefit.

## Date

2026-08-18