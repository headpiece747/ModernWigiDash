# ADR-0011 Addendum: three more process-wide singletons wear the same pattern

**Date:** 2026-09-27
**Status:** Accepted (addendum to ADR-0011; amends the registered debt, supersedes nothing)
**Deciders:** Project owner

## Context

ADR-0011 registers process-wide mutable global state as deliberate debt, and
its Decision, its Consequences and its Exit plan are written about exactly two
members: `LhmSensorStore` and `FrameTimeStore`, each a thin facade over one
`TelemetryStoreFacade<TRecord>` that owns a `TryReadFresh` staleness window.

The REASON it gives is general, though: widgets are constructed by reflection
and a reflection-constructed object cannot receive injected dependencies. That
reason generalises past the two stores, and three more process-wide singletons
exist for it. None of the three is a `TelemetryStoreFacade`, so none of them is
covered by the Decision's shape, the Consequences' accounting, or the Exit
plan's trigger conditions.

| Singleton | What it owns | Named by an ADR? |
| --- | --- | --- |
| `TwitchSession.Shared` (`Widgets/Twitch/TwitchAuthenticationService.cs:5`) | a `SemaphoreSlim` write gate, a live `CancellationTokenSource` for the validation tick, and the token set | no |
| `PriceFeedManager` `SharedFeed` (`Widgets/CryptoStockTickerWidget.cs:52`) | two WebSocket feed loops and three REST poll tasks (`_stockRestTask`, `_cryptoRestTask`, `_fxRestTask`) over one manager | no |
| `VendorService.Instance` (`Hardware/Service/VendorService.cs:10-18`) | a `volatile` static door to one `WigiDashServiceClient`, which owns the background-refresh cache | yes - ADR-0025 already calls itself an "ADR-0011 image" |

Two corrections to the framing that prompted this addendum, both checked against
the source rather than assumed:

1. **The vendor case is not unnamed.** ADR-0025 names the pattern in its own
   text, and ADR-0022 owns the client. What ADR-0025 does not do is count it in
   the debt, so the gap is the accounting, not the naming.
2. **`PriceFeedManager` is not a bare global.** It carries a ref-count AND a
   lifecycle gate, because its start and its teardown are two non-atomic field
   sequences and a start racing a teardown would otherwise hand out a disposed
   socket behind a live token (`PriceFeedManager.cs:96-99`, `:325-332`). Its
   30 s REST cadence, reconnect ceiling and retired-CTS grace are three
   deliberately separate policies that share a number, and the debt guard
   allow-lists all three with that reason. The risk there is registered but
   hardened, which is a different thing from ADR-0011's thin facade.

## Decision

The pattern generalises; the registered debt does not cover it. This addendum
names the three as ADR-0011-pattern singletons so the exit plan has three more
members than the ADR counted, and records that ADR-0011's own trigger
conditions do not fire for any of them. No code changes, and no change of
direction: nothing here says the pattern was wrong.

## Consequences

**Positive:**

- The exit plan is complete against the codebase it describes, so "replace the
  statics when the loader gains a dependency path" is now a bounded list of
  five rather than an open-ended worry.
- Each member's real risk is stated separately, so a future reader does not
  infer that a hardened ref-counted manager and a thin facade are the same debt.

**Negative (the debt, restated per member):**

- `TwitchSession.Shared`: the write gate and the validation tick's CTS are
  process-lifetime. There is no dispose; the teardown is a logout/clear, so a
  test that drives a session must not leave the gate held or the tick running.
- `PriceFeedManager`: ref-counted and gated, but still one manager per process,
  so ADR-0011's second trigger (two display sessions in one process) would
  share one feed and one REST cadence.
- `VendorService`: the thinnest of the three, a `volatile` pointer that is
  null-tolerant by design (a null instance degrades to the house placeholder).
  Its debt is coupling, not a resource leak, and it is the one member already
  covered by a sibling ADR.

## Exit plan

The trigger is ADR-0011's unchanged: widget construction gains a dependency path
(a DI container in the loader, a factory attribute on `[WidgetMetadata]`, or a
constructor parameter). What changes per member:

- The two stores need nothing. `TelemetryStoreFacade<TRecord>` is already
  DI-friendly, one instance per store.
- `TwitchSession` is the most ready of the three, and the DI work is ALREADY
  DONE: it has a parameterless public constructor delegating to an internal
  one that takes the token store, the client factory, the time provider and the
  browser opener. Only the delivery path is missing - widgets reach it through
  `Shared` instead of being handed an instance.
- `PriceFeedManager` needs the most: a per-session feed means one manager per
  display session, which makes the ref-count per-session by construction rather
  than by gate.
- `VendorService` is a pointer, so its seam is the client behind it, not the
  static in front of it.

## Date

2026-09-27
