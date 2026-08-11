> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`). Archived for history.

# FPS / Frame Time Widget — PresentMon-Faithful Two-View Design

Date: 2026-08-09
Status: Approved (design dialogue + browser mockups; B chosen with C on tap)

## 1. Context

The FPS / Frame Time widget shows live present metrics from the PresentMon Service
(ADR-0003). A live-telemetry investigation (play a game → metrics showed; close it →
metrics stayed; play a video → no video FPS) established the root cause:

- PresentMon counts **presents**, not content frames. Games present per-frame at their
  own rate (real, varying data). Desktop/composited content (a browser with a playing
  video, any foreground window after a game closes) presents at the compositor's vsync
  cadence, so PresentMon reports ~the monitor refresh rate for *every* foreground
  process. A video's content FPS (24/30) is physically unmeasurable via present counts.
- The producer never idles (there is always a foreground window), so the widget stayed
  in "tracked" mode showing composited desktop numbers as game-style metrics.
- A desktop-composition heuristic (`LooksLikeDesktopComposition`) was added and then
  **removed by user decision** — the user wants the widget to show exactly what
  PresentMon reports, per PresentMon's own documentation.

### User decisions (from the design dialogue)

1. **Option B** as the primary view: the extended widget layout — hero FPS + frame
   time, sparkline, and 8 metric cards — with labels/units corrected to PresentMon's
   documented metric definitions.
2. **Tap toggles to Option C**: the PresentMon-overlay-style readout (metric lines),
   restyled with the project font (Geist) and the widget's accent/text colors — not
   PresentMon's monospace-green look.
3. **Graceful shrinking**: both views degrade by hiding elements as the placement
   shrinks (the existing tiny/width-threshold pattern).
4. **PresentMon-faithful display only**:
   - The "monitor refresh rate as FPS" display is **removed** — PresentMon has no such
     feature.
   - No process tracked → the readout renders with **"—"** values (PresentMon's
     overlay has nothing to render; no fabricated numbers).
   - Process tracked, no presents → **0 FPS** (PRESENTED_FPS = presents/sec = 0 —
     PresentMon's documented math).
   - `PRESENT_MODE` with no data → "—".
   - The `LooksLikeDesktopComposition` heuristic is removed; Chrome/desktop shows
     PresentMon's real numbers.
   - "Unavailable" (service absent) and "capture inactive" (service ETW dead)
     placeholders stay.

## 2. Metric definitions (per PresentMon docs — PresentMonAPI.h, PM_METRIC_*)

| Widget value | PresentMon metric | Stat | Unit (per PresentMon) |
|---|---|---|---|
| Hero FPS | `PRESENTED_FPS` | AVG | FPS |
| Frame time (hero) | `PRESENTED_FPS` derived (1000/AVG) | — | ms |
| 1% LOW | `PRESENTED_FPS` | PERCENTILE_99 → 1000/P99 | FPS |
| 0.1% LOW | `PRESENTED_FPS` | PERCENTILE_01 → 1000/P01 | FPS |
| GPU BUSY | `GPU_BUSY` | AVG | **percent** (fixes today's "ms" mislabel) |
| CPU FRAME | `CPU_FRAME_TIME` | AVG | ms |
| DISPLAYED | `DISPLAYED_FPS` | AVG | FPS |
| DROPPED | `DROPPED_FRAMES` | AVG (window count) | count |
| GPU TIME | `GPU_TIME` | AVG | ms |
| PRESENT MODE | `PRESENT_MODE` | NEWEST | enum → name |

`PRESENT_MODE` values (PM_PRESENT_MODE): Unknown; Hardware Legacy Flip; Hardware
Legacy Copy to Front Buffer; Hardware Independent Flip; Composed Flip; Composed Copy
with GPU GDI; Composed Copy with CPU GDI; Hardware Composed: Independent Flip.

Short-label mapping for cards (full names in view C):

| Enum id | View C (full) | View B (short) |
|---|---|---|
| 0 | Unknown | Unknown |
| 1 | Hardware Legacy Flip | HW Legacy Flip |
| 2 | Hardware Legacy Copy to Front Buffer | HW Copy to Front |
| 3 | Hardware Independent Flip | HW Ind. Flip |
| 4 | Composed Flip | Composed Flip |
| 5 | Composed Copy with GPU GDI | Comp. Copy (GPU) |
| 6 | Composed Copy with CPU GDI | Comp. Copy (CPU) |
| 8 | Hardware Composed: Independent Flip | HWC Ind. Flip |
| other | — | — |

## 3. Data model (ModernWigiDash.Sdk + Widgets)

`FrameTimeSnapshotDto` (Sdk/DataModels/FrameTimeSnapshotDto.cs):

- Rename `GpuBusyMs` → `GpuBusyPercent` (double, PresentMon's documented unit).
- Add `DisplayedFps` (double), `DroppedFrames` (int), `GpuTimeMs` (double),
  `PresentMode` (string — PresentMon canonical name, empty when no data).

`FrameTimeSnapshotRecord` (Widgets/FrameTimeStore.cs): mirror the same rename +
additions (positional record gains the four fields before `RecentFrameTimesMs`).

`FrameTimeStore.UpdateFromDto`: map the new fields; no staleness changes.

## 4. Producer (App/PresentMon/PresentMonFrameTimeProducer.cs)

- Dynamic query grows from 5 to 9 metrics: `PRESENTED_FPS` (AVG/P99/P01),
  `CPU_FRAME_TIME`, `GPU_BUSY`, `GPU_TIME`, `DISPLAYED_FPS`, `DROPPED_FRAMES`,
  `PRESENT_MODE`, `APPLICATION`. 1 s poll cadence unchanged.
- `GPU_BUSY` maps to `GpuBusyPercent` (no ms conversion).
- `PRESENT_MODE` maps enum id → name via a pure `PresentModeLabel` mapping
  (unknown id → "—"); no-data → empty string.
- `DROPPED_FRAMES` maps to the window count.
- No producer-state or grace/capture-health changes.

## 5. Widget (Widgets/FrameTimeWidget.cs)

### Removals
- `LooksLikeDesktopComposition` + its render gate.
- `DrawMonitorMode`, the `MonitorRefreshRateHz` lazy, and the `DevMode` P/Invoke
  (all monitor-rate machinery — no longer used).

### State
- Internal `_overlayView` bool (default false = view B). `OnTouch` tap toggles it
  (in-memory only; resets to B on app restart; no export/persistence changes).
  Physical-device taps already route to widgets in runtime mode.

### View B (primary)
- Header: process name (top-right, hidden in tiny mode).
- Hero: big FPS (`F0`), "FPS" accent label, frame time (`F1` ms).
- Sparkline (recent frame times) — hidden below 150 px height.
- Metric cards, two rows (the "stack" arrangement per user instruction — GPU BUSY
  sits next to CPU FRAME and directly above PRESENT MODE):

  | 1% LOW | 0.1% LOW | CPU FRAME | GPU BUSY |
  |---|---|---|---|
  | DISPLAYED | DROPPED | GPU TIME | PRESENT MODE |

- Shrink rules: row-2 cards hide below 520 px width; all cards hide below 410 px
  width (hero stays); process name hides below 150 px height.

### View C (on tap)
- Nine lines, PresentMon's metric names: Presented FPS, Displayed FPS, 99th %tile
  Frame Time, 1st %tile Frame Time, GPU Busy %, GPU Time, CPU Frame Time, Dropped
  Frames, Present Mode — rendered with Geist + widget accent/text colors.
- Frame times derived as 1000/percentile-fps (`F1` ms), matching PresentMon's stat
  naming (99th/1st %tile).
- Shrink rules: font scales with bounds; lines clip bottom-up — below 150 px height
  keep the first 4 lines, below 110 px keep only Presented FPS.

### No-data rendering (both views)
- `ProcessId <= 0` (no process tracked): render the current view's structure with
  "—" values; no process name, no fabricated numbers.
- Tracked but no presents (`Fps == 0`): show "0" exactly as PresentMon computes.
- `PresentMode` empty → "—".

### Error states (unchanged)
- `!IsAvailable` → "Frame capture unavailable / Install and run the PresentMon
  Service" placeholder.
- `!CaptureHealthy` → "PresentMon capture inactive" placeholder.

## 6. Testing (ModernWigiDash.Tests, MSTest)

- **Producer seam tests** (PresentMonFrameTimeProducerTests): new metrics map
  correctly (DisplayedFps, DroppedFrames, GpuTimeMs); `GPU_BUSY` → percent; the
  existing P99/P01/game/idle/video-scenario pins stay green.
- **PresentModeLabel mapping**: every PM_PRESENT_MODE id → expected name; unknown → "—".
- **Widget state tests** (FrameTimeWidgetStateTests):
  - Tap toggles `_overlayView`; both views render without throwing.
  - No-process snapshot renders dashes (replaces the monitor-mode test).
  - Tracked-idle (`Fps=0`) renders "0" without throwing.
  - Both views render at small sizes (shrink rules) without throwing.
  - Composite-detection tests removed with the heuristic.
- **Telemetry mapping tests** (TelemetryStoreMappingTests): rename + new fields.
- Full suite (build + `dotnet test`, temp output per repo convention) must stay green.

## 7. Verification on the physical WigiDash

1. Launch; no foreground process → widget shows dashes (no "162 FPS").
2. Play a game → real FPS + all 8 cards (GPU BUSY in %); PRESENT MODE shows the
   game's actual mode.
3. Tap the widget → C view (project font/colors); tap again → B.
4. Resize to a small placement → cards/lines hide gracefully in both views.
5. Foreground Chrome with a video → its PresentMon numbers + "Composed Flip".
6. Close the game → widget follows the new foreground process (PresentMon data).

## 8. Out of scope

- LHM-style per-sensor widgets fed by PresentMon telemetry (`GPU_TEMPERATURE`,
  `GPU_MEM_USED`, `CPU_UTILIZATION`, … are available via the API — future work).
- Persisting the view toggle across restarts.
- Changing the 1 s poll cadence or PresentMon session/window settings.
