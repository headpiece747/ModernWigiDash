# ADR-0007: One fetch-flow module owns the weather fetch sequence

**Date:** 2026-08-18
**Status:** Accepted
**Deciders:** Project owner

## Context

The weather widget's fetch path — `FetchLiveWeatherAsync` plus its helpers
(`StillCurrent`, `LoadCachedWeatherAsync`, the `CanFetch` forward, the
inspector-candidates stamp) — spelled the SEQUENCE around one fetch across
five widget methods, about 150 lines, held together by comments (the
2026-08-18 architecture review, `docs/reports/architecture-review-20260818-190821.html`,
Card 1):

1. **The capture window**: the query key captured BEFORE the await, then the
   fetch run through `WeatherClient.FetchCurrentAsync` (which owns its own,
   narrower window through the cache-save await).
2. **Two drop gates the client's window cannot see**: the outcome key vs. the
   start key (an edit landing between the flow's two location reads resolves a
   different identity the live re-check cannot see) and the post-await live
   re-check (an edit landing in the return→apply gap, including the
   post-`InitializeAsync` profile hydration).
3. **The drop-and-refetch routing**: a dropped (`DroppedStale`) result
   force-refetches the NEW identity immediately — the in-flight claim that
   swallowed the edit-time force refresh is released by the completion.
4. **The write-back gating**: the resolved label queues its UI-thread
   write-back only when no `CustomLabel` supplies the title, under an
   identity re-check whose check + set was one critical section on the
   widget's gate.
5. **The cadence gate** and **the boot-load rollback**: the throttle window +
   static-snapshot veto for every non-forced cadence source, and the
   discarded-cache-load rollback of the client's committed resolution state
   (`WeatherClient.LoadCacheAsync`'s state-commitment contract — the
   rejection is the caller's job).

Every one of these rules was testable only through a live widget instance
(driving its HTTP, its render tick, and its gate), and ADR-0006's second
inline identity spelling (the widget's `StillCurrent` re-derivation) lived in
that same mass.

## Decision

**The fetch sequence has one owner: `ModernWigiDash.Widgets.WeatherFetchFlow`.**

- One entry point per concern: `CanFetch(force)` is the single "fetch if due"
  gate for every cadence source (force → always; static snapshot + an existing
  fetch stamp → never on a non-forced cadence; else the client's throttle
  window), `RunFetchAsync(force)` is one run of the flow, `RunBootLoadAsync`
  is the boot cache load. The caller's obligation is one line.
- The capture-window order — key captured before the await, re-validated
  after, twice — is enforced in code in the module, not by comments at a call
  site. Both drop gates route through `WeatherQueryKey.SameKey` (ADR-0006's
  ONE predicate); ADR-0006's second inline spelling is absorbed here.
- The host concerns stay with the widget: the property coercion
  (`BuildLocation`), the gate discipline around the display state (the apply
  and version-read seams run under the display-state module's gate — the later
  `WeatherDisplayState` extraction moved that discipline out of the widget
  into its own module), the UI-thread write-back flush, and the context
  requests — handed to the module as seams (the client is the cluster's real
  module, not a seam; the resolved-identity twin crossed as a constructor
  parameter originally, and a later extraction removed it — the inspector
  stamp is built from the applied payload, so the flow's only view of the
  host is the seam).
- The widget's `FetchLiveWeatherAsync` / `LoadCachedWeatherAsync` are
  forwards; `FetchLiveWeatherAsync` returns the flow's verdict
  (`WeatherFetchFlowOutcome`: `Applied` / `DroppedStale` / `Skipped` /
  `Cancelled`).
- The test surface is the module interface: `WeatherFetchFlowTests` pins the
  applied path, both drop gates (each with the forced re-fetch landing on the
  NEW identity), the write-back gating, the cadence gate, and the boot-load
guards (version, identity, rollback, missing/cancelled) — without a widget
   instance, a render tick, or the host's gate. The test host wraps the SAME
   `WeatherDisplayState` module the widget uses (the later extraction that
   moved the gate discipline into the display-state module), so the flow is
   tested against the production gate shape, not a mirror of it.

## Consequences

**Positive:**
- The ADR-0006 invariant gains its final guard site an owner: no widget-method
  comment is load-bearing anywhere in the cluster.
- The capture-window order, the drop routing, and the rollback are assertable
  at the module interface (17 new interface tests; the flow's two drop gates
  are now pinned against seam drift, not against a live widget's timing).
- The widget shrinks by ~150 lines of policy to host concerns only (property
  coercion, gate, flush, context requests) — the same shape the other
  presentation modules already give the widget.
- `WeatherClient` keeps its own narrower capture window (through the
  cache-save await) intact — the module composes with it instead of
  duplicating it.

**Negative:**
- One more module in the cluster (the widget and the flow both exist while
  the flow is being built: a two-phase extraction, committed in one step).
- The seams are an implicit contract: the host must keep running the apply /
  version / write-back seams under its gate. The mirror-image test host and
  the widget ctor keep this honest, but it is a discipline, not a type.

## Alternatives considered

1. **Keep the sequence in the widget, test through a live widget instance** —
   the existing state. The five-method mass stayed comment-held, the two drop
   gates stayed timing-dependent in tests, and ADR-0006's second spelling kept
   living inside the mass.
2. **Split the widget into two partials (fetch + host)** — partials change the
   file layout, not the test surface: the sequence would still only be
   drivable through a widget instance, and the second `SameKey` spelling would
   still need a home.
3. **Push the flow into `WeatherClient`** — the client owns the fetch LEGS
   (resolution, HTTP, cache, throttle); the write-back flush, the render
   requests, and the display-state gate are host concerns the client cannot
   own without gaining a UI dependency. The flow composes the client as a
   module and keeps the host seams separate.

## Date

2026-08-18