# ADR-0016: The font caches evict by whole-cache reset past a declared cap (no LRU)

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

The `FontHelper` memoizations are keyed on content variety: glyph presence
per (typeface handle, codepoint), fallback typeface per (codepoint, style
value), text-run splits per (text, style value, preferred typeface handle),
cached fonts per (typeface handle, size key), and per-font meta per typeface
handle. Content variety means an unbounded key space: any widget text is a
key. The five caches are bounded by one shared rule
(`FontCacheEviction`): strictly over its own declared cap, clear the whole
cache and let it refill on demand.

## Decision

No LRU, deliberately. Every entry in all five caches is a pure recompute: no
side effects, no unmanaged lifetime beyond the process-lifetime typeface
caches, no IO. An evicted entry costs exactly one recompute on next use, and
an LRU would pay bookkeeping on every hot-path access (the 30 FPS
draw/measure path) to save recomputes that only happen at overflow. Each
cache's cap is declared exactly once in the module, so the five bounded
caches cannot drift from one another; `FontCacheEvictionTests` pins the
exclusive greater-than boundary and the whole-reset eviction, and
`FramePipelineAllocationTests` pins the consumer side (the per-tick
allocation budget), so a recompute storm shows up as a budget failure.

## Exit plan

Add a per-entry LRU, or a size budget with per-entry cost, when overflow
becomes observable. A new cache that joins the shared rule with side effects
(a handle, an IO, a cross-thread commitment) does not meet the cost-neutral
premise of the whole reset and must not join the rule as-is.

Trigger conditions:

1. An overflow observed in a log, counter, or on-device behavior (a recompute
   visible to the user).
2. A `FramePipelineAllocationTests` budget failure attributed to font
   recomputes.
3. A boundary constant in `FontCacheEvictionTests` moves (cap changes are
   review decisions, and they re-open the LRU question).
4. A side-effecting cache asks to join the shared rule (the premise breaks;
   that cache gets its own policy).

## Consequences

**Positive:**

- Zero bookkeeping on the hot path: the draw/measure path pays no eviction
  cost between overflows.
- One rule, five caches, one tested boundary: the caps cannot drift from one
  another or from the tested rule.
- An evicted entry costs exactly one recompute, and the recomputes are the
  pure functions the module exists to memoize.

**Negative (the debt this ADR registers):**

- A content-diverse workload (many distinct text runs at tick rate) can
  churn the cache: overflow, whole clear, refill, overflow again. The
  recompute storm is bounded by the caps, but it is a real cost an LRU would
  spread out.
- The whole reset evicts hot entries alongside cold ones. Right after an
  overflow, the next draws pay recomputes for keys that were hot.

## Date

2026-08-24