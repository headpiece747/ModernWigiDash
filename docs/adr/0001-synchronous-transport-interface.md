# ADR-0001: Synchronous Transport Interface

**Date:** 2026-08-06  
**Status:** Accepted  
**Deciders:** Project owner  

## Context

The transport layer (`IDisplayTransport`) was originally designed with an async interface wrapping synchronous USB I/O. Every transport method was `ValueTask<T>`, implemented as `ValueTask.FromResult(syncBody())`, forcing callers to bridge with `.GetAwaiter().GetResult()` or `await`. This "fake async" pattern affected:
- `ModernWigiDashDisplayService` (WCF service) — 12 sync-over-async bridges
- `DisplayHardwareWorkerService` — awaited every transport call
- `DisplayDeviceEngine` — mixed sync/async with additional timeout logic

The async interface was a half-migration: the USB operations (WinUSB/LibUsbDotNet control transfers, bulk writes) are inherently blocking with no I/O completion port support.

## Decision

**Make `IDisplayTransport` fully synchronous.** Remove all `ValueTask<T>` wrappers and `CancellationToken` parameters from transport methods. Callers use the sync methods directly.

## Consequences

**Positive:**
- Eliminates 12 sync-over-async bridges in the WCF service (simpler code, no deadlocks)
- Removes fake `ValueTask.FromResult` wrappers (removes ~40 lines of dead async plumbing)
- Transport methods now correctly represent their behavior: blocking USB I/O
- Callers don't need to be async (WCF service is naturally sync; worker uses Task.Run internally)
- Removes `CancellationToken` parameters that were never used (all 11 of them)

**Negative:**
- No migration path to future async USB I/O if/when LibUsbDotNet adds async bulk transfers (unlikely for control transfers; conceivable for bulk writes)
- Callers that want async behavior must wrap in `Task.Run` (already done in the worker)
- Breaks any external consumer that may depend on the async signatures (none exist in this codebase)

**Rationale:** The transport layer's blocking nature is inherent to USB HID/WinUSB — there's no I/O completion port model for these APIs. Wrapping synchronous I/O in `ValueTask.FromResult` adds cognitive overhead, prevents the C# compiler from detecting sync-over-async, and forces every caller to reason about async semantics that don't exist.

## Alternatives considered

1. **Keep fake async + suppress `S6966` warnings** — maintained the illusion but carried debt everywhere. Rejected because the debt is real and growing.
2. **Migrate USB I/O to truly async** — WinUSB has no async bulk write API; LibUsbDotNet's overlapped write is documented as "problematic" (the WinUsbNative.cs comment). Not feasible without a major transport rewrite.
3. **Provide both sync and async overloads** — doubles the API surface with no practical benefit since the async version is always fake. Rejected for the same reason as (1).

## Date

2026-08-06
