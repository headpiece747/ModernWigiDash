# Hot-Path Debt Triage: Frame Pipeline

**Date:** 2026-08-24
**Scope:** `ModernWigiDash.Hardware/Transport/`, `ModernWigiDash.Core/Rendering/` (SkiaFrameCompositor + companions), `ModernWigiDash.App/FramePump.cs` + the MainWindow tick wiring.
**Question:** where does debt compound fastest, and what is the cheapest next move at each hot spot?

## Method

1. **Churn measurement** (git, all history): the repo is 33 days old (first commit 2026-07-21, 494 commits, 492 of them in August), so all-time churn and recent churn are the same signal.
2. **Semantic scan** (Glider): cyclomatic complexity >= 12 per project, unused symbols per hot path (Private/Internal, heuristics on), project dependency graph.
3. **Two read-only specialist audits** (subagents): a performance-analyst pass over the frame pipeline threading/allocations/resources, and a dotnet-architect pass over coupling/seams/policy ownership.
4. **First-hand verification** of every top-line finding against on-disk source (see Verification log). One audit finding (P8) was downgraded on verification.
5. **health-check scoped** to the three paths (not a full 8-dimension project grade): build from the gate log, code quality from complexity + audits, architecture from the project graph, dead code from the unused-symbol scan, API surface from the architect pass.
6. **opportunity-scan, adapted:** the skill's window-of-logs mode needs the agentmemory MCP, which is not connected in this session. The code-domain equivalent was run instead: the git `fix:` commit sequence is the window of logs, and the recurring themes in it are the "what keeps happening" signal.

## Churn map

Path commit counts (33 days): `ModernWigiDash.App/` 184, `ModernWigiDash.Sdk/` 66, `ModernWigiDash.Hardware/Transport/` 57, `ModernWigiDash.Core/Rendering/` 41.

Top production files by commits:

| Commits | File | In scope |
|---|---|---|
| 81 | App/MainWindow.xaml.cs | yes (tick wiring) |
| 65 | Widgets/WeatherForecastWidget.cs | no |
| 44 | Widgets/WeatherClient.cs | no |
| 39 | Widgets/PriceFeedManager.cs | no |
| 33 | Hardware/Transport/DisplayHidTransport.cs | yes |
| 31 | Hardware/Transport/DisplayDeviceEngine.cs | yes |
| 28 | Sdk/FrameDelivery.cs | yes (feeds the path) |
| 20 | Hardware/Transport/WinUsbNative.cs | yes |
| 16 | Core/Rendering/SkiaFrameCompositor.cs | yes |
| 7 | App/FramePump.cs | yes (recent extraction, stable since) |

Churn character matters as much as volume: the transport's 57 commits are 16 `fix:` vs 3 `feat:`, a hardening-dominated sequence (latency 94ms to 56ms, frame copy eliminated, encode-into-pool, connect verdict covering the init bulk write, standby verdict read from the task result, close-bound clamped). The video's premise (debt in high-churn files compounds) is visible directly in this sequence: each audit round landed on the transport again.

Out of scope, for the record: the weather cluster (109 commits across the two production files, 26 `fix:` vs 14 `feat:`, driven by ADR-0006..0009) is the co-equal next candidate if this triage is extended.

## Triage

Ranked by churn x hot-path exposure x confidence. Effort is rough.

### Tier 1: top priority

**T1 (perf P1) CONTENTION: one USB lock serializes the 16ms touch read behind the ~55ms frame write.**
`DisplayHidTransport.cs` (verified 2026-08-24):
- `SendFrame` (lines 544-612) holds `lock (_usbLock)` across the header control-out, the ~55ms bulk OUT, and the abort control-out on failure.
- `ReadTouch` (lines 449-509) holds the same lock across the whole control-IN.
- Both loops run on thread-pool threads (sender: `FrameDelivery.cs:127`; touch poll: `PollLoop.cs:52`), so this is not a UI-thread stall, but touch sample ingestion queues behind the write. With a 55ms write roughly every 55ms cycle, the lock is held nearly continuously while frames flow: average touch-read wait ~27ms, worst ~55ms. The in-order drain still delivers the full Down/Move/Up sequence, but time-compressed.
- The lock exists for teardown safety ("the handle must never be freed while a transfer could be in flight", lines 511-523), but the steady-state path needs no mutual exclusion between a control read and a bulk write.
Fix sketch: keep the lock only for backend swap/teardown. Track in-flight transfers with an `Interlocked` counter (increment before the transfer, decrement in `finally`); `TearDownWinUsb`/`Cleanup` take the swap lock and briefly wait for zero in-flight. Steady-state `SendFrame`/`ReadTouch` go lock-free; the invariant holds by construction instead of by one coarse lock. Effort: half a day plus the contention pin from "What to encode" below.

**T2 (perf P2) UI-THREAD CPU: the 601,472-pixel RGB565 encode runs on the dispatcher thread inside `Push`.**
`FrameDelivery.Push` (verified 2026-08-24, lines 224-261) calls `_encoder.Encode(frame, buffer)` (line 250) on the caller thread before `Queue`. The caller is the FramePump tick on the UI thread (MainWindow wiring, `composeAndSend`). It is allocation-clean (writes into the pooled buffer), but it is several milliseconds of dispatcher CPU on roughly half of all ticks. It sits on the UI thread because the compositor owns exactly one `SKBitmap` (verified `SkiaFrameCompositor.cs:15`, exposed as `FrameBuffer` at line 36): the sender cannot encode a bitmap the compositor has already moved on to.
Fix options: (a) benchmark the encode now (15 minutes, instrument `FrameEncoder.ConvertToRgb565` at `WinUsbNative.cs:394-401` style zero-alloc timing) to size the problem, then (b) rotate 2-3 SKBitmaps in the compositor so `Push` can hand the bitmap reference to the sender and the encode overlaps the previous write. The ring depth must exceed the in-flight frames (channel capacity 4 + 1 sender slot, per the pool's `capacity + 1` margin comment at `FrameDelivery.cs:94-102`); with drain-to-latest coalescing the effective depth is much smaller, so the ring can be sized from measured in-flight, not worst case. Effort: (a) 15 min, (b) 1-2 days with tests.

### Tier 2: real, bounded

**T3 (perf P3) PACING: the device ceiling is ~15-18 FPS, but the pump still ticks at 30 and works on skipped ticks.**
The compose gate (`MainWindow.xaml.cs:355`, `!_delivery.IsSendInFlight`) vetoes compose while the previous write is in flight, and a ~55ms write cannot fit the 33ms cadence, so back-to-back sends space at ~55ms (pacing measured from send start, `FrameDelivery.cs:283-294`). Meanwhile `FramePump.Tick` (verified in full, 76 lines) always runs `_requestRepaint()` (InvalidateVisual) and `_onTick` (badge) even on a vetoed tick: pure WPF work for a frame that will not change.
Fix: a decision, not code pressure. Either accept and document the ~18 FPS on-device ceiling and slow the pump to the write cadence (saves the skipped ticks' repaint + badge work), or make T2's ring the overlap and feed the device at its true maximum. Effort: low once decided.

**T4 (perf P4) EDIT-MODE ALLOCATION: the selection badge creates a native SKFont and a string every compose tick.**
`EditOverlay.DrawSelection` (verified, lines 88-111): `using var font = FontHelper.CreateFont(_uiTypeface, 12f)` + an interpolated `badgeText` per frame whenever `editMode && isSelected`. The comment claims "edit-mode-only (not the 30 FPS path)", which is false for the selected case: that is exactly the state the user is actively authoring in, and the App's edit-mode checkbox defaults checked. EditOverlay already hoists its six paints (lines 26-57), so the font is the one thing left behind.
Fix: hoist the 12px badge font through `FontHelper.GetCachedFont` (the house cache under `FontCacheEviction`) and memoize `badgeText` per `(DisplayName, ZIndex)`. Effort: ~30 min.

**T5 (arch A1) POLICY LOCATION: the frame-readiness predicate is spelled in the App bind site.**
`MainWindow` binds `isReady: () => _usbDevice.State == ConnectionState.Connected` (the "FrameDelivery" startup step, line ~174). Readiness is a delivery policy (it drives `DroppedNotReadyCount`), but its truth lives in the engine's state machine. If readiness ever widens (e.g. buffer during `Connecting`), the App bind site must change and a second site agrees with the engine by discipline.
Fix: add `bool CanSendFrames => State == ConnectionState.Connected` on `DisplayDeviceEngine` (it already owns the single `ConnectionState` truth); the bind site becomes `isReady: _usbDevice.CanSendFrames`; pin the agreement in a test. Effort: ~30 min.

### Tier 3: cheap batch

**T6 (perf P5)** `UpdateUsbBadge` (verified `MainWindow.xaml.cs:911-921`) builds `brushKey + label` every tick just to change-detect. Key the detection on `_usbDevice.State` (the enum) and build the string only when the enum changed. ~15 min.

**T7 (perf P7)** Per-frame `GCHandle.Alloc(Pinned)`/`Free` in `WinUsbBulkDevice.BulkWrite` (verified lines 388-424) and a capturing diagnostic lambda per write (`WinUsbNative.cs:403`, `LibUsbTransferBackend.cs:241`) that allocates its display class even when the `DiagLog` cadence suppresses the line. Fix: `fixed (byte* p = data)` in an unsafe block (the buffer is always a plain `byte[]`), and move the diagnostic to a string built only in the failure branch (the LibUsb `WriteChunk` already made this exact trade, lines 255-263). ~30 min.

**T8 (perf P6) DOCUMENTED TRADE: the touch drain holds the queue lock across gesture application.**
`DrainDeviceTouchQueue` (verified `MainWindow.xaml.cs:497-524`) feeds the gesture machine under `_deviceTouchLock`; the doc comment names the trade (the 16ms poll thread blocks during input handling instead of the UI thread marshalling N closures) and bounds it by "one gesture, a few milliseconds". The bound is right for a tap, but a swipe landing on a page switch runs `ApplyProfileMutation`'s full refresh bundle mid-drain, which is more than a few milliseconds. The poll thread (not the UI thread) pays it. Revisit after T1 (which removes the write-side contention): swap-under-lock (dequeue all pending samples under the lock, release, then feed) keeps ordering (one drain per burst) and shrinks the held window. ~1 hour plus a pin adjustment.

**T9 (perf P8, downgraded on verification) RETENTION EDGE: `WinUsbBulkDevice.Open` does not close acquired handles if an exception fires mid-open.**
The catch at `WinUsbNative.cs:371-375` logs and returns false with `_deviceHandle`/`_interfaceHandle` left open. The production call site is safe: the WinUSB provider leg disposes the backend on every failure exit (`TryCreateWinUsbBackend`, verified lines 208-237: `TearDownWinUsb` on `Open()==false`, `winUsb.Dispose()` under `_usbLock` on exception), and `Dispose` closes both handles with zeroing (lines 478-492). So there is no production leak; the debt is that `Open`'s safety depends on caller discipline. Fix: close the handles in the catch (or a finally) when open did not complete, so the method is self-contained. ~30 min plus a test through the `WinUsbApi` delegate-bag seam (an OOM-shaped throw after acquisition).

**T10 (arch A2) TEST-COUPLING: Sdk policy tests import the Hardware encoder at 16 sites.**
`FrameDeliveryTests.cs` passes `encoder: new SkiaRgb565Encoder()` in every scenario (the file also defines a `FixedSizeEncoder` fake it mostly does not use), so Sdk policy tests carry the real 1,202,944-byte output size and real pixel packing. Fix: Sdk tests use only the fake; add one pin at the Hardware boundary that `new SkiaRgb565Encoder().OutputBufferSize == DisplayGeometry.FrameBufferSize` plus one encode-into-pooled-buffer byte-count pin (house ArchitectureTests style). ~1 hour.

**T11 (arch A3/A4/A5/A6/A7) SURFACE NARROWING.** Five small seams exist only for tests or wobble across the chain: `IDisplayTransport : IAsyncDisposable` with no production caller of `DisposeAsync` (drop it, extend the ADR-0001 sync pin to "no async dispose on the seam"); `DisplayDeviceEngine.TryConnect` public with only test callers (make it `internal`, like its test-seam siblings); `FrameDelivery.IsReady` public with only test readers (make it `internal`); `InputController` references `SkiaFrameCompositor.ResizeHandleSize` by concrete type (move the const to a shared Core.Rendering geometry fact beside `WidgetRouting`); the send chain spells `byte[]` across the delivery/engine seams and `ReadOnlyMemory<byte>` at the transport, so the transport's zero-copy fast path handles a case the upstream seams can never produce (unify on `ReadOnlyMemory<byte>` end to end). ~1 hour total.

**T12 (arch D1) DOC DRIFT.** CONTEXT.md's layering table says "Hardware references Core+Sdk"; the project graph shows Hardware references Sdk only. The code is stricter than the doc; fix the line. Trivial.

### Tier 4: accepted design / watch list

- **P9:** a hung bulk write holds the sender (and the compose gate) for the full 30s pipe timeout; the display simply goes stale. Bounded, accounted, owned by the reconnect machinery. Optional: treat a write exceeding ~3x the 55ms norm as a fault and force the transport's reconnect path instead of waiting out the timeout.
- **FrameDelivery.Dispose drains a `SingleReader = true` channel** (verified lines 380-420: `TryComplete` then `Reader.TryRead` while the sender task may still be mid-read). Benign in practice (each slot is consumed once), but the contract is ambiguous: either a comment stating the race is benign or `SingleReader = false`.
- **Authoring-UI complexity** (user-driven, not per-tick): `InspectorPanelRenderer.Render` CC=19, `DialogHost.ShowIconPicker` CC=19, `PresentMonApiProbe()` CC=18, `PresentMonFrameTimeProducer.Poll` CC=13. `FrameEncoder.ConvertToRgb565` is CC=16 and is the hot-path method (subsumed by T2). The CC=37/259-line `MainWindow.Connect` is WPF-generated `IComponentConnector` code and is excluded.
- **LHM map source per-poll byte[] copy** (1 Hz, thread pool): off the 30 FPS path, accepted.

## Verified clean (do not spend effort here)

From the performance audit, each item checked against source (direct verification in the Verification log; the rest triangulated by both independent audits):

1. No synchronous USB I/O on the UI thread. Bulk write on the sender's `Task.Run` loop; touch control read on the `PollLoop` background thread; initial connect off-thread. The UI thread's pipeline work is compose, the encode (T2), and the non-blocking `TryWrite`.
2. No `.Result`/`.Wait()`/`SpinWait` on the 30 FPS or 16 ms paths. All bounded waits are shutdown-only, and the 2026-08-21 standby-verdict trap is fixed in code (`DisplayDeviceEngine.cs:468-472`: the verdict is read from `Result` only after `Wait` confirms completion).
3. The frame buffer pool cannot leak: every acquire path releases (encode failure, sender finally, coalescer drop via the hoisted delegate, queue failure, Dispose drain), and `Release` guards size and double-release so the pool never grows (`FrameBufferPool.cs:42-63`).
4. WinUSB/LibUsb handle pairing on every normal and failure path, including the orphan rule (every leg failure disposes the local device; the LibUsb leg closes on claim failure and on exception).
5. Compose is allocation-clean on the happy path: one reused `SKBitmap`/`SKCanvas`, hoisted alpha paint, background hex parse hoisted behind a change check, stack-alloc insertion sort for small pages.
6. Text/font hot paths are memoized under the bounded `FontCacheEviction` rule; `TruncateText` allocates only when truncation actually happens.
7. The WPF repaint draws the sent buffer directly (SKElement `PaintSurface`, hoisted `SKSamplingOptions`); no `WriteableBitmap`, no per-frame pixel copy.
8. The pacing channel is wakeup-safe: `WaitToReadAsync` wakes on every `TryWrite`; `DrainToLatest` keeps exactly one frame and releases every stale buffer.
9. Every pipeline invariant has one named owner: frame size (transport `SendFrame`), drop accounting (delivery), pacing (delivery `SenderLoop`), close budgets (`CloseBudgetPolicy`), touch normalization (`TouchReport.ToEventType`), standby ritual (transport `GoToStandby`), pixel area (`DisplayGeometry`, pinned through reflection `ConstValue` so the pin cannot fold into the constant).
10. Zero unused Private/Internal symbols in `Hardware/Transport` and `Core/Rendering` (Glider, heuristics on).

## Scoped health check (three core paths)

Grades per the house rubric (`.opencode/skills/health-check/references/grading-rubric.md`), scoped to the triage paths. Data-source notes where the dimension's canonical tool is unavailable here (no Glider antipattern detector or coverage map).

| Dimension | Grade | Key finding |
|---|---|---|
| Build Health | A | Last gate run 2026-08-24T02:33: 0 errors, 0 warnings, 1685 tests green |
| Code Quality | B | 0 high-confidence antipatterns (no sync-over-async, no ambient clock, no ad-hoc HttpClient; house pins in ArchitectureTests), but 3 verified per-tick quality findings (T4, T6, T7) that antipattern detectors do not see |
| Architecture | A | Correct direction, 0 project or type cycles; caveats: T5 (policy at the wrong layer), T12 (doc drift) |
| Test Coverage | A (approx) | No Glider coverage tool; every pipeline type has a named test class in the suite inventory (transport, engine, WinUsbBulkDevice, delivery, pool, coalescer, pump, close budgets, chunked write, architecture pins); 1685 green |
| Dead Code | A | 0 unused symbols (Private/Internal) in both hot paths |
| API Surface | B | T11: four members/inheritances exist only for tests or wobble across the send chain |
| Security | Not assessed | No package scan this session; last sweep 2026-08-23; T9 is the security-adjacent item and is caller-covered |
| Documentation | A | XML docs verified on all sampled pipeline public types; CONTEXT.md current except the T12 line |

GPA (7 graded dimensions): **3.71 (A-), "Excellent: production-ready, well-maintained".** The grade matches the audit verdict: the pipeline is unusually well-decoupled; the debt is small and concentrated at the edges.

## What to encode (opportunity-scan, adapted)

The transport's 16-commit `fix:` sequence (33 days) is what the window-of-logs scan is for. Its three recurring themes are already encoded in structure, which is why they stopped recurring: teardown safety (the orphan rule + `_usbLock`), verdict truthfulness (standby verdict from the task result; connect verdict covering the init bulk write), and close budgets (`CloseBudgetPolicy`). Two rules the audits surfaced are still held by discipline and comments only:

1. **The 30 FPS tick path stays allocation-light and UI-thread-light.** Currently enforced by comments ("zero-alloc render path", "avoid allocations per tick") and the 2026-08-21 one-off memory soak. No test would have caught T4 (per-frame SKFont), T6 (per-tick string), or T2 (multi-ms encode on the dispatcher). Proposed pin: a tick-budget test that warms the caches, measures `GC.GetTotalAllocatedBytes` across N compose+push ticks, and asserts below a budget (the compose happy path allocates nothing, so the budget can be tight). Would catch the T2/T4/T6 class.
2. **A touch read is never serialized behind a frame write.** Currently held by the coarse `_usbLock` doing the opposite, with no test measuring it. Proposed pin: drive the transport through the `ITransferBackend` seam with a slow fake bulk write, run a concurrent `ReadTouch` loop, and assert the read's wait time stays below a bound. Would catch the T1 class and make the fix's success measurable (before/after the contention pin is the proof).

Both are house-style test pins in the `CloseBudgetPolicyTests`/`DisplayGeometry` image: pure, seam-driven, no hardware, no pixels.

## Priority actions

1. **T1 + the contention pin** (Tier 1). Half a day to a day. The hottest file, both hot loops, user-perceivable touch latency.
2. **T2 sizing** (Tier 1). 15 minutes of instrumentation first; the ring is 1-2 days and only worth it if the measured encode cost justifies it.
3. **Cheap batch: T4, T5, T6, T7, T9, T10, T11, T12.** Under 3 hours total, each independently verifiable, each small enough to ship as one conventional commit.
4. **T3 decision** (accept-and-document the device ceiling vs. overlap via the T2 ring) and **T8** after T1 lands.
5. **Add the two encoding pins** so the T1/T2 classes cannot return silently.

## Verification log (2026-08-24, this session)

Directly verified against on-disk source:

- `DisplayHidTransport.ReadTouch` lock across the whole control-IN (449-509); `TearDownWinUsb` teardown-safety comment and lock (511-523).
- `DisplayHidTransport.SendFrame` lock across header + bulk + abort (544-612); size contract at the seam (Refused under `FrameBufferSize`).
- `WinUsbBulkDevice.Open` acquisition (308-334), pipe-timeout block (339-356), outer catch (371-375), `BulkWrite` GCHandle + diag closure (382-425), `Dispose` handle pairing (478-492).
- `TryCreateWinUsbBackend` leg disposal on both failure exits (208-237) -> P8 downgraded from leak to defense-in-depth.
- `FrameDelivery` ctor pool sizing from encoder output (83-127), channel options (120-127), `Push` encode-on-caller (224-261), `Queue` (364-373), `Dispose` drain + bounded join (380-420).
- `FrameBufferPool.Acquire`/`Release` guards (42-63).
- `PollLoop` background-thread start + `PeriodicTimer` (44-53, 94-129).
- `DisplayDeviceEngine.Dispose` standby verdict from `Result` after `Wait` confirms (432-491).
- `FramePump` full file (76 lines): tick order compose -> repaint -> badge, gate vetoes compose only, disposed guard.
- `SkiaFrameCompositor` single `SKBitmap`/`SKCanvas` (1-45); `ResizeHandleSize` forwarding const (13).
- `EditOverlay.DrawSelection` per-frame font + string with the false comment (88-111).
- `MainWindow` touch enqueue/drain lock scope + documented trade comment (455-530); `UpdateUsbBadge` per-tick string (895-925).
- Churn: per-path and per-file commit counts, fix/feat composition (git); gate rows (`.audit/gates.tsv`); complexity >= 12 per project and unused symbols per path (Glider).

Triangulated (both audits read the same lines independently; no third read taken): `SenderLoop` pacing-from-start (283-294, 305-341), `ChannelFrameCoalescer.DrainToLatest`, `WinUsbApi` delegate bag, LibUsb leg teardown (133-156, 276-289), `ChunkedBulkWrite` short-write advance, `MainWindow` bind sites (172-174, 347-356), `InputController` const read (251-252), `FrameDeliveryTests` encoder imports (16 sites), `IDisplayTransport` async-dispose surface.