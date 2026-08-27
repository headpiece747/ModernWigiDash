# ADR-0012: The frame pipeline encodes on the compose thread and does not overlap compose and send

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

The pump keeps the 30 FPS presentation contract (`FrameDelivery.FrameInterval`,
33 ms, the single frame-rate owner), but the display's full-frame bulk write
measures ~55 ms, so the device caps at ~15-18 FPS. The compose gate
(`FramePump` reads the delivery's `IsSendInFlight`) vetoes compose while a
write is in flight; the repaint and badge refresh still fire on a vetoed
tick, so the window and chrome stay live.

Two further constraints shape the pipeline: the compositor owns exactly one
SKBitmap (the frame pushed to the delivery is the one the canvas drew), and
the encode (`IRgb565Encoder`) runs on the `Push` caller's thread, the tick's
dispatcher thread, which the 16 ms touch poll shares.

A compositor-side bitmap ring was evaluated and declined (the 2026-08-24
hot-path triage). It would need a depth at or above the channel capacity plus
the sender slot (~1.2 MB of pixels per slot), and the sender would encode a
bitmap the canvas has already moved on to, breaking the
buffer-drawn-is-exactly-the-buffer-sent identity.

## Decision

The pipeline stays sequential: one owned bitmap, one encode on the tick
thread, the bounded DropOldest channel, drain-to-latest, paced send. The
device ceiling is documented: the pump keeps its 30 FPS contract and the
device is fed at its measured ceiling. The encode's dispatcher cost is pinned
by a timing canary: `FrameEncoderPixelTests.ConvertToRgb565_FullFrame_StaysUnderTheTimeCanary`
bounds a full-frame encode under 50 ms against the 33 ms cadence, so a
regression on the tick's thread fails a test instead of showing up as frame
hitches. `FramePipelineAllocationTests` pins the allocation side (the
per-tick budget after the 2026-08-24 font-churn fix).

## Exit plan

Revisit overlap (a bitmap ring, or a second compose thread) only when the
measured write time falls below about twice the cadence, or the canary starts
failing. Any overlap design must keep the buffer-drawn-is-exactly-the-buffer-sent
identity: the sender encodes the very bitmap the canvas drew.

Trigger conditions:

1. The timing canary fails (a full-frame encode over 50 ms).
2. A protocol change halves the frame payload or the measured bulk write time
   (a partial-update command, a smaller framebuffer, a faster pipe).
3. An on-device measurement attributes visible hitches to the encode (the
   allocation canary pins bytes; a cost that is neither bytes nor allocation
   shows up here first).

## Consequences

**Positive:**

- The buffer-drawn-is-exactly-the-buffer-sent identity holds by construction:
  the compositor's single bitmap flows straight into the encode.
- No per-slot pixel memory (a ring at the required depth is ~1.2 MB or more
  per slot) and no cross-thread handoff of a live bitmap.
- The tick-thread cost is bounded and observable: the timing canary plus the
  allocation budget.

**Negative (the debt this ADR registers):**

- Every accepted tick pays the encode on the UI thread. A slow encode
  starves the touch poll directly (same dispatcher).
- The on-device frame rate is ~half the nominal 30 FPS. The ceiling is
  accepted, not hidden.
- The canary is a time bound on shared machines, so it must stay generous
  (50 ms against the 33 ms cadence) or CI goes flaky.

## Revisit notes

2026-08-27 (parked-resolution pass): the exit conditions were re-checked and
no trigger fired. The timing canary is green (a full-frame encode stays
under the 50 ms bound), the protocol is unchanged (same frame payload,
same measured ~55 ms bulk write), and no on-device measurement attributes a
visible hitch to the encode. One reading note on trigger 1: the measured
write (~55 ms) is already below twice the cadence (66 ms), so that trigger
reads as "the write becomes cheap enough that the ring's identity cost no
longer outweighs the benefit", not as a threshold the pipeline has crossed.
The binding constraint stays the
buffer-drawn-is-exactly-the-buffer-sent identity. Decision unchanged.

## Date

2026-08-24