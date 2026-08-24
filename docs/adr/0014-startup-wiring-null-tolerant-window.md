# ADR-0014: The window's pre-module wiring window is null!-typed and null-tolerant

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

`MainWindow` constructs its modules in an ordered sequence (`StartupWiring`:
one named artifact the constructor applies in order, the startup image of the
`TeardownPlan`). The order is load-bearing knowledge pinned against the real
list by `StartupWiringTests`: the host modules before the profile load (a
widget's `InitializeAsync` runs synchronously inside the load and calls back
into the context), the state resyncs before the wired arm (so their XAML
events stay guarded), the wired arm strictly last.

The XAML-backed fields the wiring assigns (`_framePump`, `_powerLifecycle`,
`_telemetry`, `_profile`, `_inputController`, `_deviceTouchDrain`,
`_delivery`, `_inspector`, `_dialogHost`, `_pageTabs`, `_profilePersistence`)
are `null!`-typed: during the pre-module window they are type-visible and
unassigned. The context's module-deref callbacks (`MainWindow.Context.cs`)
are null-tolerant for that window, so an event that fires before its module
exists degrades to a benign no-op instead of the historical startup NRE.

## Decision

The type-level debt (the `null!` assertions the compiler cannot prove) is
accepted in exchange for one construction sequence and one sequence pin,
instead of a two-phase construction (a separate pre-wired facade type, or a
lazy module resolver). The null-tolerance in the context callbacks is the
safety net that makes the debt benign.

## Exit plan

Move to two-phase construction when the pre-module surface grows, or when a
reorder degrades to a no-op in a way the sequence pin cannot see: a
pre-wiring facade that exposes only what the XAML events need (the modules
behind it), or a lazy module resolver that hands out a module on first
deref. Either removes the `null!` from the fields and makes the pre-module
window a type instead of a convention.

Trigger conditions:

1. A startup NRE (the failure mode this design retired).
2. A new pre-module XAML event or reader (the no-op tolerance gains a
   consumer, and each consumer is another silent path).
3. A reorder test that must assert a no-op instead of an order (the pin no
   longer measures the invariant it was named for).

## Consequences

**Positive:**

- One construction sequence, one test that pins it. Reorders are caught at
  the gate, not on a user's machine.
- A pre-module event is a benign no-op, not a crash: the window shows, the
  module arms, the next event works.
- No second window type and no facade surface to keep in sync with the real
  one.

**Negative (the debt this ADR registers):**

- The `null!` fields are assertions the compiler cannot check. A reader must
  know the wiring order to reason about first use.
- A pre-module handler fails silently (the guarded no-op). A bug in the
  pre-module path is invisible until the module exists.
- The null-tolerance in the context callbacks is itself a convention: a new
  module-deref callback that forgets it reintroduces the NRE this design
  retired, and the sequence pin does not cover it.

## Date

2026-08-24