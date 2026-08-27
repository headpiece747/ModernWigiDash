# ADR-0013: Deliberate DebtGuard exceptions are allow-list entries with reasons and exit conditions

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

The mechanical debt rules are pinned in `DebtGuardTests` and run in the
gate's test stage: sync-over-async only at the documented budgeted sites,
`async void` only on event handlers, every handle-acquiring file carries its
disposal evidence in the same file, the frame pipeline has one encode entry
and one buffer pool, and no dead private helpers. A small number of sites
deliberately sit on the other side of one of those rules. Without a record
of why, a reviewer cannot tell a deliberate exception from an oversight, and
the allow-list entries in the test have no owner for the why.

## Decision

Per-site exceptions live as allow-list entries in the pin (file + reason),
drift-checked both directions: a retired site fails the gate until the entry
is removed, and a new site fails until an entry with a reason lands in the
same commit. This ADR is the umbrella record: it holds the rationale and the
exit condition for every entry, so the test stays the mechanical layer and
the why has one owner.

Sync-over-async entries (`HouseRules_SyncOverAsync_OnlyAtTheDocumentedBudgetedSites`):

- `ModernWigiDash.Hardware/Transport/DisplayDeviceEngine.cs`: the standby
  verdict is read only once `Wait` confirms completion, the
  dispose-abandon budget reads the transport's `CloseBudgets`
  (`CloseBudgetPolicy`), and the connect-fault continuation runs on a
  threadpool thread. Exit: when ADR-0001 (synchronous transport) is
  superseded and the transport becomes awaitable, the close-path waits lose
  their reason and the entry retires with them.
- `ModernWigiDash.Core/Models/ProfileOps.cs`: the profile load runs the
  widgets' `InitializeAsync`/`Dispose` synchronously inside the load. A
  synchronous user action (import, startup), not a tick. Exit: when the load
  moves off the UI thread (a background load with a progress surface), the
  entry retires.
- `ModernWigiDash.Sdk/FrameDelivery.cs` (1 s), `ModernWigiDash.Sdk/PollLoop.cs`
  (5 s), `ModernWigiDash.Widgets/FeedLoop.cs` (5 s): the bounded stop waits
  are the loop shape's shutdown contract. Exit: when the lifecycle becomes
  an awaitable stop with a caller that can await it. Until then the budget is
  the invariant; a larger budget is a review decision, not an accident.

Handle-disposal entries (`HouseRules_HandleAcquiringFiles_CarryTheirDisposalEvidence`):

- `ModernWigiDash.App/WindowChrome.cs`: one DWM window-attribute call. The
  marker regex sees the extern; the call acquires and owns no handle. Exit:
  only when the P/Invoke leaves the file.
- `ModernWigiDash.App/PresentMon/PresentMonLoader.cs`: the process-lifetime
  PresentMon service DLL (`NativeLibrary.Load`). The handle is deliberately
  not freed: the DLL belongs to the service directory, and the
  client-service binary protocol is not backward-guaranteed (issue #383), so
  the app never ships its own copy. Exit: a decision to ship a client-side
  DLL copy, which changes the ownership rule and turns the entry into a real
  leak to fix.
- `ModernWigiDash.Widgets/HotkeyActionExecutor.cs`: the SendInput P/Invoke
  fires input events and acquires and owns no handle. The entry suppresses
  the marker regex. Exit: only when the P/Invoke leaves the file.
- `ModernWigiDash.App/Hotkey/HotkeyApi.cs` (ADR-0019): the
  RegisterHotKey/UnregisterHotKey P/Invoke registers and releases
  message-loop hotkeys. The marker regex sees the extern, but the calls
  acquire and own no handle: the registration identity is a caller-chosen
  id in the OS message loop, released through UnregisterHotKey on the
  owning manager's dispose path (`GlobalHotkeyManager`), not in this
  delegate bag. Exit: only when the P/Invoke leaves the file.

## Consequences

**Positive:**

- A new exception is a deliberate, gate-enforced, reviewed edit with a
  reason; the allow-list cannot grow silently.
- The register cannot shrink silently either: a stale entry fails the gate.
- The per-site reason sits next to the rule it excepts, and the why has one
  owner (this ADR).

**Negative:**

- The gate failure message is the first documentation a reader sees;
  understanding an exception takes the test and this ADR.
- The umbrella form (one ADR for many entries) deviates from the house's
  one-decision-per-ADR grain. It is accepted because the entries are one
  pattern (deliberate exceptions to a mechanical rule), and splitting them
  into seven ADRs would scatter a single invariant.

## Trigger conditions

1. A new sync-over-async site or a handle file without evidence appears: the
   gate refuses the commit until an entry + reason lands (or the code moves
   to the compliant shape).
2. An allow-list entry goes stale: the gate refuses the commit until the
   entry is removed.
3. An on-device UI stall is attributed to a sync site: that entry's reason is
   no longer true, and the site becomes a fix (or a standalone ADR) in the
   same change that moves it.

## Date

2026-08-24