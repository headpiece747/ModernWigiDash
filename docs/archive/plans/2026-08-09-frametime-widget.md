# FPS / Frame Time Widget — PresentMon-Faithful Two-View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the FPS / Frame Time widget to show only PresentMon-reported data (per PresentMon's documentation): an 8-metric dashboard (view B) that tap-toggles to a PresentMon-overlay-style readout (view C), both shrinking gracefully, with the monitor-refresh-rate display and the desktop-composition heuristic removed.

**Architecture:** Extends the existing PresentMon pipeline (App producer → Sdk DTO → Widgets store → widget render). The dynamic query grows from 4 to 8 elements; the DTO/record gain 4 fields and correct the GPU_BUSY unit (percent, not ms); the widget drops the monitor machinery and renders two views behind an in-memory tap toggle.

**Tech Stack:** .NET 10, C# 14 (file-scoped namespaces, records, collection expressions, switch expressions), WPF/SkiaSharp, MSTest.

**Spec:** `docs/superpowers/specs/2026-08-09-frametime-widget-design.md` (commit `2e2d445`).

## Global Constraints

- .NET 10 / C# 14 conventions: file-scoped namespaces, one type per file, file name = type name, `sealed` unless designed for inheritance, records for DTOs, collection expressions, switch expressions over if-chains.
- MSTest only (never xUnit): `[TestClass]`/`[TestMethod]`, AAA, naming `Method_Scenario_ExpectedResult`, one assertion concept per test.
- No new NuGet packages. No changes to the transport/session/poll cadence, the grace-window logic, or `TrackedTargetResolver`.
- Widgets are reflection-instantiated: no ctor injection; static stores stay.
- Test build must use temp output: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`.
- Repo state note: the working tree carries uncommitted heuristic-era changes (the `LooksLikeDesktopComposition` rule + its tests + a CONTEXT.md sentence). Task 3 removes them; the CONTEXT.md sentence is replaced in Task 5. Do not commit them separately.

---

### Task 1: Sdk data model — present-mode labels, DTO/record/store fields

**Files:**
- Create: `ModernWigiDash.Sdk/DataModels/PresentMonPresentMode.cs`
- Modify: `ModernWigiDash.Sdk/DataModels/FrameTimeSnapshotDto.cs`
- Modify: `ModernWigiDash.Widgets/FrameTimeStore.cs`
- Modify: `ModernWigiDash.Tests/TelemetryStoreMappingTests.cs`
- Modify: `ModernWigiDash.Tests/UnitTestSuite.cs:558-577`
- Modify: `ModernWigiDash.Tests/FrameTimeWidgetStateTests.cs` (compile fixes only: the `Composite` helper and `Render_CompositeSignature_*` are deleted in Task 3; here just fix the `GpuBusyMs` reference at line 153)

**Interfaces:**
- Produces: `PresentMonPresentMode.FullName(int id) -> string` and `PresentMonPresentMode.ShortName(int id) -> string` (Sdk).
- Produces: `FrameTimeSnapshotDto` with `GpuBusyPercent` (double, replaces `GpuBusyMs`), plus `DisplayedFps` (double), `DroppedFrames` (int), `GpuTimeMs` (double), `PresentModeId` (int, default -1 = no data).
- Produces: `FrameTimeSnapshotRecord` positional record with the same fields inserted after `CpuFrameTimeMs`: `double GpuBusyPercent, double CpuFrameTimeMs, double DisplayedFps, int DroppedFrames, double GpuTimeMs, int PresentModeId, IReadOnlyList<double> RecentFrameTimesMs, ...`.
- Consumes (unchanged): `FrameTimeSnapshotDto` field order for the store mapping.

> Implementation note (explicit deviation from the spec's "PresentMode string" wording): the DTO/record carry `PresentModeId` (int) instead of the canonical-name string. Rationale: one id source, both label forms (full for view C, short for view B cards) derived at the single mapping site `PresentMonPresentMode`, "—" handled uniformly for id -1. The spec's label table is implemented exactly by `PresentMonPresentMode`.

- [ ] **Step 1: Write the failing tests for the label mapping**

Create `ModernWigiDash.Tests/PresentMonPresentModeTests.cs`:

```csharp
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class PresentMonPresentModeTests
{
    [TestMethod]
    public void FullName_EveryPresentMonId_MapsToCanonicalName()
    {
        Assert.AreEqual("Unknown", PresentMonPresentMode.FullName(0));
        Assert.AreEqual("Hardware Legacy Flip", PresentMonPresentMode.FullName(1));
        Assert.AreEqual("Hardware Legacy Copy to Front Buffer", PresentMonPresentMode.FullName(2));
        Assert.AreEqual("Hardware Independent Flip", PresentMonPresentMode.FullName(3));
        Assert.AreEqual("Composed Flip", PresentMonPresentMode.FullName(4));
        Assert.AreEqual("Composed Copy with GPU GDI", PresentMonPresentMode.FullName(5));
        Assert.AreEqual("Composed Copy with CPU GDI", PresentMonPresentMode.FullName(6));
        Assert.AreEqual("Hardware Composed: Independent Flip", PresentMonPresentMode.FullName(8));
    }

    [TestMethod]
    public void FullName_UnknownId_ReturnsDash()
    {
        Assert.AreEqual("—", PresentMonPresentMode.FullName(-1));
        Assert.AreEqual("—", PresentMonPresentMode.FullName(7));
        Assert.AreEqual("—", PresentMonPresentMode.FullName(999));
    }

    [TestMethod]
    public void ShortName_EveryPresentMonId_MapsToCompactLabel()
    {
        Assert.AreEqual("Unknown", PresentMonPresentMode.ShortName(0));
        Assert.AreEqual("HW Legacy Flip", PresentMonPresentMode.ShortName(1));
        Assert.AreEqual("HW Copy to Front", PresentMonPresentMode.ShortName(2));
        Assert.AreEqual("HW Ind. Flip", PresentMonPresentMode.ShortName(3));
        Assert.AreEqual("Composed Flip", PresentMonPresentMode.ShortName(4));
        Assert.AreEqual("Comp. Copy (GPU)", PresentMonPresentMode.ShortName(5));
        Assert.AreEqual("Comp. Copy (CPU)", PresentMonPresentMode.ShortName(6));
        Assert.AreEqual("HWC Ind. Flip", PresentMonPresentMode.ShortName(8));
    }

    [TestMethod]
    public void ShortName_UnknownId_ReturnsDash()
    {
        Assert.AreEqual("—", PresentMonPresentMode.ShortName(-1));
        Assert.AreEqual("—", PresentMonPresentMode.ShortName(999));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "FullyQualifiedName~PresentMonPresentModeTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: build error `CS0246: The type or namespace name 'PresentMonPresentMode' could not be found`.

- [ ] **Step 3: Create the mapping**

Create `ModernWigiDash.Sdk/DataModels/PresentMonPresentMode.cs`:

```csharp
namespace ModernWigiDash.Sdk;

/// <summary>
/// PM_PRESENT_MODE value → label mapping (PresentMonAPI.h). The id is the
/// PresentMon enum value as polled from the dynamic query; the widget stores
/// the id in the snapshot and derives both display forms from this single
/// mapping site. "-1" (no data) and unknown ids render as "—".
/// </summary>
public static class PresentMonPresentMode
{
    public static string FullName(int id) => id switch
    {
        0 => "Unknown",
        1 => "Hardware Legacy Flip",
        2 => "Hardware Legacy Copy to Front Buffer",
        3 => "Hardware Independent Flip",
        4 => "Composed Flip",
        5 => "Composed Copy with GPU GDI",
        6 => "Composed Copy with CPU GDI",
        8 => "Hardware Composed: Independent Flip",
        _ => "—",
    };

    public static string ShortName(int id) => id switch
    {
        0 => "Unknown",
        1 => "HW Legacy Flip",
        2 => "HW Copy to Front",
        3 => "HW Ind. Flip",
        4 => "Composed Flip",
        5 => "Comp. Copy (GPU)",
        6 => "Comp. Copy (CPU)",
        8 => "HWC Ind. Flip",
        _ => "—",
    };
}
```

- [ ] **Step 4: Run the label tests to verify they pass**

Run: the same filter command as Step 2.
Expected: 4 passed.

- [ ] **Step 5: Update the DTO**

In `FrameTimeSnapshotDto.cs`: rename the `GpuBusyMs` property to `GpuBusyPercent` (doc: "GPU busy percentage for this process's present work (PM_METRIC_GPU_BUSY, %).") and add the four new properties after `CpuFrameTimeMs`:

```csharp
    /// <summary>
    /// Average GPU busy time per frame for this process's work, in milliseconds
    /// (PM_METRIC_GPU_TIME, "Ms GPU Time").
    /// </summary>
    public double GpuTimeMs { get; set; }

    /// <summary>
    /// Presented frames per second (PM_METRIC_DISPLAYED_FPS) — presented minus
    /// dropped frames.
    /// </summary>
    public double DisplayedFps { get; set; }

    /// <summary>
    /// Frames dropped in the window (PM_METRIC_DROPPED_FRAMES).
    /// </summary>
    public int DroppedFrames { get; set; }

    /// <summary>
    /// PM_PRESENT_MODE enum value for the tracked swap chain, or -1 when no
    /// data. Display labels via <see cref="PresentMonPresentMode"/>.
    /// </summary>
    public int PresentModeId { get; set; } = -1;
```

Also update the `GpuBusyPercent` doc comment: "GPU busy percentage (PM_METRIC_GPU_BUSY, documented as %)."

- [ ] **Step 6: Update the record and store mapping**

In `FrameTimeStore.cs`, the positional record becomes:

```csharp
public sealed record FrameTimeSnapshotRecord(
    bool IsAvailable,
    int ProcessId,
    string ProcessName,
    double Fps,
    double FrameTimeMs,
    double Low1PercentFps,
    double Low01PercentFps,
    double GpuBusyPercent,
    double CpuFrameTimeMs,
    double DisplayedFps,
    int DroppedFrames,
    double GpuTimeMs,
    int PresentModeId,
    IReadOnlyList<double> RecentFrameTimesMs,
    DateTime LastUpdate = default,
    bool CaptureHealthy = true)
```

`UpdateFromDto` maps the renamed/new fields:

```csharp
                dto?.GpuBusyPercent ?? 0,
                dto?.CpuFrameTimeMs ?? 0,
                dto?.DisplayedFps ?? 0,
                dto?.DroppedFrames ?? 0,
                dto?.GpuTimeMs ?? 0,
                dto?.PresentModeId ?? -1,
                dto?.RecentFrameTimesMs ?? [],
```

- [ ] **Step 7: Update compile sites + mapping tests**

- `TelemetryStoreMappingTests.cs` `FrameTimeStore_UpdateFromDto_MapsAllMetrics`: set `GpuBusyPercent = 71.0` (instead of `GpuBusyMs = 45.3`), add `DisplayedFps = 144.0, DroppedFrames = 2, GpuTimeMs = 5.1, PresentModeId = 4`, and assert `rec.GpuBusyPercent`, `rec.DisplayedFps`, `rec.DroppedFrames`, `rec.GpuTimeMs`, `rec.PresentModeId` (same values).
- `UnitTestSuite.cs` `FrameTimeStore_UpdateAndRead_RoundTrips`: replace `GpuBusyMs: 92.0,` with `GpuBusyPercent: 92.0,` and `Assert.AreEqual(92.0, read.GpuBusyMs)` → `read.GpuBusyPercent`.
- `FrameTimeWidgetStateTests.cs` line ~153: `GpuBusyMs = 0.3` → `GpuBusyPercent = 0.3` (the composite tests referencing it are removed in Task 3).

- [ ] **Step 8: Run the full suite**

Run: the full temp-output test command from Global Constraints.
Expected: all pass, including the four new label tests (the suite's remaining heuristic-era tests are removed by Task 3).

- [ ] **Step 9: Commit**

```bash
git add ModernWigiDash.Sdk/DataModels/PresentMonPresentMode.cs ModernWigiDash.Sdk/DataModels/FrameTimeSnapshotDto.cs ModernWigiDash.Widgets/FrameTimeStore.cs ModernWigiDash.Tests/PresentMonPresentModeTests.cs ModernWigiDash.Tests/TelemetryStoreMappingTests.cs ModernWigiDash.Tests/UnitTestSuite.cs ModernWigiDash.Tests/FrameTimeWidgetStateTests.cs
git commit -m "feat(sdk): present-mode labels and frame-time DTO/record metric corrections"
```

---

### Task 2: App — poll the four new PresentMon metrics and map them

**Files:**
- Modify: `ModernWigiDash.App/PresentMon/PresentMonNativeInterop.cs` (constants)
- Modify: `ModernWigiDash.App/PresentMon/IPresentMonNative.cs` (sample record)
- Modify: `ModernWigiDash.App/PresentMon/PresentMonNative.cs:223-242,167-171` (elements + poll mapping)
- Modify: `ModernWigiDash.App/PresentMon/PresentMonFrameTimeProducer.cs:123-137` (DTO mapping)
- Test: `ModernWigiDash.Tests/PresentMonFrameTimeProducerTests.cs`

**Interfaces:**
- Consumes: `PresentMonProtocol` constants, `FrameTimeSnapshotDto` fields from Task 1, `PresentMonPresentMode` (not needed here — the DTO carries the raw id).
- Produces: `PresentMonDynamicSample(double Fps, double Low1PercentFps, double GpuBusyPercent, double CpuFrameTimeMs, double DisplayedFps, int DroppedFrames, double GpuTimeMs, int PresentModeId)`.
- Produces: `FrameTimeSnapshotDto` with all fields populated from the sample.

- [ ] **Step 1: Write the failing producer-mapping test**

In `PresentMonFrameTimeProducerTests.cs`, replace `AvailableNative()`'s sample with all fields and extend `Poll_TracksProcessAndMapsSampleToDto`:

```csharp
    private static FakePresentMonNative AvailableNative()
    {
        return new FakePresentMonNative
        {
            PollResult = new PresentMonDynamicSample(143.2, 110.4, 71.0, 4.05, 142.8, 2, 6.1, 4),
            FrameTimes = [6.5, 6.7],
        };
    }
```

```csharp
        Assert.AreEqual(71.0, dto.GpuBusyPercent, 0.001, "GPU busy is a percent metric (PM_METRIC_GPU_BUSY); no conversion");
        Assert.AreEqual(4.05, dto.CpuFrameTimeMs, 0.001);
        Assert.AreEqual(142.8, dto.DisplayedFps, 0.001);
        Assert.AreEqual(2, dto.DroppedFrames);
        Assert.AreEqual(6.1, dto.GpuTimeMs, 0.001);
        Assert.AreEqual(4, dto.PresentModeId);
```

Also update the two other `new PresentMonDynamicSample(` call sites (tests `Poll_VideoPlayerPresentingAtLowFps_MapsFpsThrough` and `Poll_DataArrivesAfterUnhealthy_Recovers`) to the 8-arg ctor — e.g. `new PresentMonDynamicSample(23.97, 22.1, 2.1, 1.2, 23.9, 1, 2.0, 4)` and `new PresentMonDynamicSample(120.0, 100.0, 0.5, 3.0, 119.8, 0, 4.0, 8)`.

- [ ] **Step 2: Run the producer tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "FullyQualifiedName~PresentMonFrameTimeProducerTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: build error (sample record mismatch / missing `GpuBusyPercent`).

- [ ] **Step 3: Add the protocol constants**

In `PresentMonNativeInterop.cs`, add (ids are the PM_METRIC enum values from PresentMonAPI.h, verified against the header):

```csharp
    public const uint MetricDisplayedFps = 11;
    public const uint MetricGpuTime = 13;
    public const uint MetricDroppedFrames = 16;
    public const uint MetricPresentMode = 20;
```

- [ ] **Step 4: Extend the sample record**

In `IPresentMonNative.cs`:

```csharp
public sealed record PresentMonDynamicSample(
    double Fps,
    double Low1PercentFps,
    double GpuBusyPercent,
    double CpuFrameTimeMs,
    double DisplayedFps,
    int DroppedFrames,
    double GpuTimeMs,
    int PresentModeId);
```

Update the record's doc comment: units match the API's metric table — GPU busy is a percent (PM_METRIC_GPU_BUSY); PRESENT_MODE carries the raw PM_PRESENT_MODE enum id.

- [ ] **Step 5: Grow the dynamic query and poll mapping**

In `PresentMonNative.cs`, the `dynamicElements` array becomes 8 elements:

```csharp
        var dynamicElements = new[]
        {
            new PresentMonQueryElement(PresentMonProtocol.MetricPresentedFps, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricPresentedFps, PresentMonProtocol.StatPercentile01, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricGpuBusy, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricCpuFrameTime, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricDisplayedFps, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricGpuTime, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricDroppedFrames, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricPresentMode, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
        };
```

`ChainStrideBytes` is computed from the array, so no other stride changes. In `PollDynamic`, the sample becomes:

```csharp
            var sample = new PresentMonDynamicSample(
                Fps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[0]),
                Low1PercentFps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[1]),
                GpuBusyPercent: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[2]),
                CpuFrameTimeMs: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[3]),
                DisplayedFps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[4]),
                GpuTimeMs: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[5]),
                DroppedFrames: (int)PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[6]),
                PresentModeId: (int)PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[7]));
```

- [ ] **Step 6: Map the new fields in the producer**

In `PresentMonFrameTimeProducer.cs`, the success DTO gains:

```csharp
                GpuBusyPercent = poll.Sample.GpuBusyPercent,
                CpuFrameTimeMs = poll.Sample.CpuFrameTimeMs,
                DisplayedFps = poll.Sample.DisplayedFps,
                DroppedFrames = poll.Sample.DroppedFrames,
                GpuTimeMs = poll.Sample.GpuTimeMs,
                PresentModeId = poll.Sample.PresentModeId,
```

Also update the two stale doc comments that say "monitor-refresh mode" (`Poll` summary and `CaptureDead` summary) to say "the widget's no-process (dash) state" / "instead of presenting fabricated values".

- [ ] **Step 7: Run the producer tests**

Run: the filter command from Step 2.
Expected: all pass.

- [ ] **Step 8: Run the full suite**

Run: the full temp-output test command.
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add ModernWigiDash.App/PresentMon/PresentMonNativeInterop.cs ModernWigiDash.App/PresentMon/IPresentMonNative.cs ModernWigiDash.App/PresentMon/PresentMonNative.cs ModernWigiDash.App/PresentMon/PresentMonFrameTimeProducer.cs ModernWigiDash.Tests/PresentMonFrameTimeProducerTests.cs
git commit -m "feat(presentmon): poll displayed fps, dropped frames, gpu time, present mode"
```

---

### Task 3: Widget — remove monitor/heuristic machinery, add the 8-metric view B

**Files:**
- Modify: `ModernWigiDash.Widgets/FrameTimeWidget.cs` (whole render path)
- Test: `ModernWigiDash.Tests/FrameTimeWidgetStateTests.cs`

**Interfaces:**
- Consumes: `FrameTimeSnapshotRecord` fields from Task 1, `PresentMonPresentMode.ShortName(int)` (Sdk).
- Produces: `FrameTimeWidget.Render` behavior: placeholders (unavailable/inactive) unchanged; `ProcessId <= 0` renders the tracked layout with "—" values; tracked renders view B (hero + process name + 2×4 metric cards + sparkline) with shrink rules.
- Produces (Task 4 hook): `private void RenderTrackedView(...)` and `private void RenderDashView(...)` helper methods, plus `private string _overlayView` NOT yet — the view switch is Task 4.

- [ ] **Step 1: Write the failing state tests**

In `FrameTimeWidgetStateTests.cs`, delete the six `LooksLikeDesktopComposition_*` tests and the `Composite(...)` helper, and replace the deleted `Render_CompositeSignature_RendersMonitorModeWithoutThrowing` with these:

```csharp
    private static void RenderWith(FrameTimeSnapshotDto dto, out FrameTimeWidget widget)
    {
        FrameTimeStore.UpdateFromDto(dto);
        var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        widget = new FrameTimeWidget();
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        surface.Dispose();
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void Render_NoProcessTracked_RendersDashLayoutWithoutThrowing()
    {
        RenderWith(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = -1,
            LastUpdate = DateTime.UtcNow,
        }, out _);

        Assert.IsNotNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void Render_TrackedIdleProcess_ShowsZeroWithoutThrowing()
    {
        RenderWith(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 0,
            LastUpdate = DateTime.UtcNow,
        }, out _);

        Assert.IsNotNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void Render_FullMetrics_ShowsAllEightCardsWithoutThrowing()
    {
        RenderWith(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 162.4,
            FrameTimeMs = 6.16,
            Low1PercentFps = 138.0,
            Low01PercentFps = 121.0,
            GpuBusyPercent = 71.0,
            CpuFrameTimeMs = 5.2,
            DisplayedFps = 162.0,
            DroppedFrames = 3,
            GpuTimeMs = 6.1,
            PresentModeId = 8,
            RecentFrameTimesMs = [6.5, 6.7, 6.4],
            LastUpdate = DateTime.UtcNow,
        }, out _);

        Assert.IsNotNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5)));
    }
```

The existing `RenderWith(DateTime, bool, bool, int, double)` helper stays (used by the three placeholder tests and `Render_IdleMonitorMode_RendersWithoutThrowing` — rename that test to `Render_NoProcess_RendersWithoutThrowing`).

- [ ] **Step 2: Run the state tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "FullyQualifiedName~FrameTimeWidgetStateTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: fails — `Render_NoProcessTracked_RendersDashLayoutWithoutThrowing` renders the old monitor mode (still passes), `Render_FullMetrics_ShowsAllEightCardsWithoutThrowing` passes (old layout renders 4 cards, no exception). The real failure: `LooksLikeDesktopComposition` references were deleted from the test file but still exist in the widget — no compile error yet. The meaningful gate: after Step 3 the dash branch must render without the monitor machinery (compile error in Step 3 if `MonitorRefreshRateHz` is referenced). Run this step only to confirm the suite is green before the rewrite.

- [ ] **Step 3: Rewrite the widget render path**

In `FrameTimeWidget.cs`:

Remove entirely: `LooksLikeDesktopComposition` (the method added last session), its render gate, `DrawMonitorMode`, the `MonitorRefreshRateHz` lazy, the `DevMode` struct, the `EnumDisplaySettingsW` P/Invoke, and the `System.Runtime.InteropServices` using.

Replace the `Render` body's tail (from the `ProcessId <= 0` check onward) with:

```csharp
        if (snapshot.ProcessId <= 0)
        {
            RenderDashView(canvas, bounds, accent, text);
            return;
        }

        RenderTrackedView(canvas, bounds, accent, text, snapshot);
    }

    /// <summary>
    /// No process tracked (desktop / own window foreground): the layout renders
    /// with "—" values — PresentMon has no data to show and its overlay renders
    /// nothing. No fabricated numbers.
    /// </summary>
    private void RenderDashView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);
        float heroTop = bounds.Top + pad;
        float heroH = Math.Max(8f, bounds.Height - pad * 2f);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawTextWithFallback("—", bounds.Left + pad, heroTop + fpsFontSize * 0.82f, fpsFont, fpsPaint);

        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawTextWithFallback("FPS", bounds.Left + pad + fpsFont.MeasureText("—", fpsPaint) + 10f,
            heroTop + fpsFontSize * 0.38f, unitFont, unitPaint);

        if (bounds.Width >= 410f)
        {
            var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
            using var labelPaint = new SKPaint { Color = accent, IsAntialias = true };
            var valueFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 15f);
            using var valuePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            float cardTop = heroTop + fpsFontSize * 0.82f + 12f;
            float colWidth = (bounds.Width - pad * 2f) / 4f;
            string[] labels = ["1% LOW", "0.1% LOW", "CPU FRAME", "GPU BUSY"];
            for (int i = 0; i < labels.Length; i++)
            {
                float cx = bounds.Left + pad + colWidth * (i + 0.5f);
                float valW = valueFont.MeasureText("—", valuePaint);
                canvas.DrawTextWithFallback("—", cx - valW / 2f, cardTop + 13f, valueFont, valuePaint);
                float lblW = labelFont.MeasureText(labels[i], labelPaint);
                canvas.DrawTextWithFallback(labels[i], cx - lblW / 2f, cardTop + 13f + 20f, labelFont, labelPaint);
            }
        }
    }
```

Then add the tracked view (view B). It replaces the old hero/metric/sparkline code, now with two 4-wide card rows. Keep `RefreshCachedStrings`, `DrawCachedSparkline`, and `DrawMetricCard` (extend `DrawMetricCard` usage only; it is unchanged):

```csharp
    private void RenderTrackedView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text, FrameTimeSnapshotRecord snapshot)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);

        bool tiny = bounds.Height < 150f;
        bool showCards = bounds.Width >= 410f;
        bool showSecondRow = bounds.Width >= 520f;
        bool showGraph = bounds.Height >= 150f && snapshot.RecentFrameTimesMs.Count >= 2;
        float graphHeight = showGraph ? bounds.Height * 0.12f : 0f;

        float contentTop = bounds.Top + pad;
        float contentBottom = bounds.Bottom - pad - (showGraph ? graphHeight + 6f : 0f);

        float heroTop = contentTop;
        if (!tiny && ShowProcess && !string.IsNullOrWhiteSpace(snapshot.ProcessName))
        {
            float procSize = Math.Clamp((contentBottom - contentTop) * 0.08f, 10f, 15f);
            var processFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, procSize);
            using var processPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            string process = TextRenderHelper.TruncateText(snapshot.ProcessName, processFont, bounds.Width - pad * 2f);
            canvas.DrawTextWithFallback(process, bounds.Right - pad - FontHelper.MeasureTextWithFallback(process, processFont), contentTop + procSize, processFont, processPaint);
            heroTop = contentTop + procSize + 6f;
        }

        float heroBottom = showCards ? contentTop + (contentBottom - contentTop) * 0.45f : contentBottom;
        float heroH = Math.Max(8f, heroBottom - heroTop);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        RefreshCachedStrings(snapshot);
        string fpsText = _cachedFpsText;
        fpsFont.MeasureText(fpsText, out var fpsBounds, fpsPaint);
        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawTextWithFallback(fpsText, fpsX, fpsBaseline, fpsFont, fpsPaint);

        float unitX = fpsX + fpsBounds.Width + 10f;
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawTextWithFallback("FPS", unitX, heroTop + fpsFontSize * 0.38f, unitFont, unitPaint);

        var msFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.36f);
        using var msPaint = new SKPaint { Color = text.WithAlpha(220), IsAntialias = true };
        canvas.DrawTextWithFallback(_cachedMsText, unitX, fpsBaseline, msFont, msPaint);

        if (showCards)
        {
            float gridTop = heroBottom + 4f;
            float gridH = contentBottom - gridTop;
            if (gridH >= 24f)
            {
                float colWidth = (bounds.Width - pad * 2f) / 4f;
                float metricValSize = Math.Clamp(gridH * 0.40f, 12f, 32f);
                float metricLblSize = Math.Clamp(gridH * 0.25f, 9f, 18f);
                float row1Top = gridTop;
                float row2Top = gridTop + gridH * 0.52f;

                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 0.5f, row1Top, "1% LOW", _cachedLow1, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 1.5f, row1Top, "0.1% LOW", _cachedLow01, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 2.5f, row1Top, "CPU FRAME", _cachedCpu, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 3.5f, row1Top, "GPU BUSY", _cachedGpu, metricValSize, metricLblSize, accent);

                if (showSecondRow)
                {
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 0.5f, row2Top, "DISPLAYED", _cachedDisplayed, metricValSize, metricLblSize, accent);
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 1.5f, row2Top, "DROPPED", _cachedDropped, metricValSize, metricLblSize, accent);
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 2.5f, row2Top, "GPU TIME", _cachedGpuTime, metricValSize, metricLblSize, accent);
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 3.5f, row2Top, "PRESENT MODE", _cachedPresentMode, metricValSize, metricLblSize, accent);
                }
            }
        }

        if (showGraph)
        {
            SKRect graphArea = new SKRect(bounds.Left + pad, bounds.Bottom - pad - graphHeight, bounds.Right - pad, bounds.Bottom - pad);
            DrawCachedSparkline(canvas, graphArea, snapshot.RecentFrameTimesMs, accent);
        }
    }
```

Extend the cached-string fields and `RefreshCachedStrings`:

```csharp
    private string _cachedDisplayed = "";
    private string _cachedDropped = "";
    private string _cachedGpuTime = "";
    private string _cachedPresentMode = "";
```

```csharp
        _cachedGpu = $"{snapshot.GpuBusyPercent:F0}%";
        _cachedCpu = $"{snapshot.CpuFrameTimeMs:F1} ms";
        _cachedDisplayed = $"{snapshot.DisplayedFps:F0} FPS";
        _cachedDropped = snapshot.DroppedFrames.ToString(CultureInfo.InvariantCulture);
        _cachedGpuTime = $"{snapshot.GpuTimeMs:F1} ms";
        _cachedPresentMode = snapshot.PresentModeId >= 0
            ? PresentMonPresentMode.ShortName(snapshot.PresentModeId)
            : "—";
    }
```

- [ ] **Step 4: Run the state tests to verify they pass**

Run: the filter command from Step 2.
Expected: all pass (dash layout, idle-zero, full-metrics render without throwing).

- [ ] **Step 5: Run the full suite**

Run: the full temp-output test command.
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.Widgets/FrameTimeWidget.cs ModernWigiDash.Tests/FrameTimeWidgetStateTests.cs
git commit -m "feat(widget): 8-metric frame-time dashboard with dash state, drop monitor mode and composite heuristic"
```

---

### Task 4: Widget — view C overlay readout + tap toggle

**Files:**
- Modify: `ModernWigiDash.Widgets/FrameTimeWidget.cs`
- Test: `ModernWigiDash.Tests/FrameTimeWidgetStateTests.cs`

**Interfaces:**
- Consumes: `RenderTrackedView`/`RenderDashView` from Task 3, `PresentMonPresentMode.FullName(int)` (Sdk), `IModernWidget.OnTouch(SKPoint, TouchEventType)`.
- Produces: private `bool _overlayView`; `OnTouch` toggle on `TouchEventType.TouchUp`; `RenderOverlayView(...)`; the `Render` dispatch: `if (_overlayView) { RenderOverlayView(...); return; }` placed after the unavailable/inactive placeholders and before the dash/tracked split — the overlay view renders the same dashes for `ProcessId <= 0`.

- [ ] **Step 1: Write the failing toggle + overlay tests**

Append to `FrameTimeWidgetStateTests.cs`:

```csharp
    [TestMethod]
    public void OnTouch_Tap_TogglesOverlayView()
    {
        var widget = new FrameTimeWidget();
        widget.OnTouch(default, TouchEventType.TouchUp);
        Assert.IsTrue(widget.IsOverlayView, "a tap must switch to the overlay readout");
        widget.OnTouch(default, TouchEventType.TouchUp);
        Assert.IsFalse(widget.IsOverlayView, "a second tap must switch back");
    }

    [TestMethod]
    public void OnTouch_TouchDown_DoesNotToggle()
    {
        var widget = new FrameTimeWidget();
        widget.OnTouch(default, TouchEventType.TouchDown);
        Assert.IsFalse(widget.IsOverlayView);
    }

    [TestMethod]
    public void Render_OverlayView_RendersLinesWithoutThrowing()
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 162.4,
            FrameTimeMs = 6.16,
            Low1PercentFps = 138.0,
            Low01PercentFps = 121.0,
            GpuBusyPercent = 71.0,
            CpuFrameTimeMs = 5.2,
            DisplayedFps = 162.0,
            DroppedFrames = 3,
            GpuTimeMs = 6.1,
            PresentModeId = 4,
            LastUpdate = DateTime.UtcNow,
        });

        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var widget = new FrameTimeWidget { IsOverlayView = true };
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        FrameTimeStore.Reset();

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_OverlayView_SmallSize_RendersWithoutThrowing()
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 60.0,
            Low1PercentFps = 55.0,
            Low01PercentFps = 50.0,
            GpuBusyPercent = 30.0,
            CpuFrameTimeMs = 2.0,
            DisplayedFps = 60.0,
            DroppedFrames = 0,
            GpuTimeMs = 3.0,
            PresentModeId = 4,
            LastUpdate = DateTime.UtcNow,
        });

        using var smallSurface = SKSurface.Create(new SKImageInfo(200, 160));
        var widget = new FrameTimeWidget { IsOverlayView = true };
        widget.Render(smallSurface.Canvas, new SKRect(0, 0, 200, 160));
        FrameTimeStore.Reset();

        Assert.IsNotNull(smallSurface);
    }
```

These tests need `internal` access to the view state: add to `FrameTimeWidget.cs`:

```csharp
    /// <summary>Test seam: current view (false = dashboard, true = overlay readout).</summary>
    internal bool IsOverlayView { get; set; }
```

- [ ] **Step 2: Run the state tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "FullyQualifiedName~FrameTimeWidgetStateTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: `OnTouch_Tap_TogglesOverlayView` fails — the widget has no OnTouch override yet (base virtual is a no-op, so `IsOverlayView` stays false).

- [ ] **Step 3: Implement the toggle and the overlay view**

In `FrameTimeWidget.cs`:

```csharp
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp)
        {
            IsOverlayView = !IsOverlayView;
            Context?.RequestRender();
        }
    }
```

In `Render`, after the `!snapshot.CaptureHealthy` placeholder and before the dash/tracked split:

```csharp
        if (IsOverlayView)
        {
            RenderOverlayView(canvas, bounds, accent, text, snapshot);
            return;
        }
```

Add the overlay renderer (Geist font, widget accent/text colors, PresentMon metric names):

```csharp
    /// <summary>
    /// PresentMon-overlay-style readout (view C): the metric lines PresentMon's
    /// own overlay lists, in the project font and the widget's colors. Frame
    /// times derive from the percentile FPS values, matching PresentMon's
    /// 99th/1st %tile stat naming. Lines clip from the bottom as the placement
    /// shrinks.
    /// </summary>
    private void RenderOverlayView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text, FrameTimeSnapshotRecord snapshot)
    {
        bool dash = snapshot.ProcessId <= 0;
        float pad = Math.Clamp(bounds.Height * 0.06f, 8f, 20f);
        float fontSize = Math.Clamp(bounds.Height * 0.052f, 10f, 24f);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
        using var labelPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        using var valuePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float lineHeight = fontSize * 1.45f;

        int maxLines = bounds.Height < 110f ? 1 : bounds.Height < 150f ? 4 : 9;
        int lines = Math.Min(maxLines, dash ? 1 : 9);

        string F1(double v) => v > 0 ? $"{v:F1} ms" : "—";
        string F0(double v) => v > 0 ? $"{v:F0}" : "—";

        string[] labels =
        [
            "Presented FPS", "Displayed FPS", "99th %tile Frame Time", "1st %tile Frame Time",
            "GPU Busy %", "GPU Time", "CPU Frame Time", "Dropped Frames", "Present Mode",
        ];
        string[] values =
        [
            dash ? "—" : $"{snapshot.Fps:F0}",
            dash ? "—" : F0(snapshot.DisplayedFps),
            dash ? "—" : F1(1000.0 / snapshot.Low1PercentFps),
            dash ? "—" : F1(1000.0 / snapshot.Low01PercentFps),
            dash ? "—" : $"{snapshot.GpuBusyPercent:F0}%",
            dash ? "—" : F1(snapshot.GpuTimeMs),
            dash ? "—" : F1(snapshot.CpuFrameTimeMs),
            dash ? "—" : snapshot.DroppedFrames.ToString(CultureInfo.InvariantCulture),
            dash ? "—" : PresentMonPresentMode.FullName(snapshot.PresentModeId),
        ];

        float x = bounds.Left + pad;
        float labelMaxWidth = bounds.Width * 0.55f;
        for (int i = 0; i < lines; i++)
        {
            float y = bounds.Top + pad + (i + 1) * lineHeight;
            canvas.DrawTextWithFallback(labels[i], x, y, font, labelPaint, SKTextAlign.Left);
            canvas.DrawTextWithFallback(values[i], bounds.Right - pad, y, font, valuePaint, SKTextAlign.Right);
        }
    }
```

(The unused `labelMaxWidth` local is removed — the fixed label column layout uses left/right alignment instead.)

- [ ] **Step 4: Run the state tests to verify they pass**

Run: the filter command from Step 2.
Expected: all pass (toggle, no-toggle-on-down, both overlay renders).

- [ ] **Step 5: Run the full suite**

Run: the full temp-output test command.
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.Widgets/FrameTimeWidget.cs ModernWigiDash.Tests/FrameTimeWidgetStateTests.cs
git commit -m "feat(widget): tap-toggle PresentMon overlay readout view"
```

---

### Task 5: Docs, full verification, on-device pass

**Files:**
- Modify: `CONTEXT.md` (FrameTimeStore row)
- No code.

- [ ] **Step 1: Update CONTEXT.md**

Replace the trailing sentence of the `FrameTimeStore` row (the `LooksLikeDesktopComposition` clause added last session) with:

> The widget renders only PresentMon-reported data: an 8-metric dashboard view (FPS/frame time hero, 1% low, 0.1% low, CPU frame, GPU busy %, displayed FPS, dropped frames, GPU time, present mode) that tap-toggles to a PresentMon-overlay-style readout; both views shrink gracefully and show "—" values when no process is tracked (no monitor-refresh-rate display — that is not a PresentMon feature).

- [ ] **Step 2: Build + full suite**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo` then the full temp-output test command.
Expected: build 0 warnings / 0 errors; all tests pass (~546: 537 + 6 toggle/overlay + 3 new state tests − 6 removed composite − 0; exact count reported by the runner).

- [ ] **Step 3: Relaunch on the physical WigiDash**

Kill the running app and relaunch elevated (use `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elevated.ps1` with `Stop-Process -Name ModernWigiDash.App -Force` and a `Start-Process` of the App exe). Then the user verifies, on the device:

1. Launch with no foreground process → widget shows dashes (no "162 FPS").
2. Play a game → real FPS + all 8 cards (GPU BUSY as %); PRESENT MODE shows the game's actual mode (likely "HWC Ind. Flip").
3. Tap the widget → view C (Geist + widget colors); tap again → back.
4. Shrink the placement → cards row 2, then cards, then graph hide (view B); overlay clips lines (view C).
5. Foreground Chrome with a video → its PresentMon numbers + "Composed Flip".
6. Close the game → widget follows the new foreground process (no stale-looking metrics, no fabricated monitor rate).

If PRESENT_MODE shows "—" or the app reports unavailable after this change, the `MetricPresentMode` element with `StatAvg` was rejected by the service — retry registration with `PresentMonProtocol.StatNone` for that element only (single-line change in `PresentMonNative.RegisterQueries`).

- [ ] **Step 4: Commit**

```bash
git add CONTEXT.md
git commit -m "docs(context): frame-time widget shows only PresentMon data across two views"
```

---

## Self-Review Notes

- **Spec coverage:** metric table (Task 1-2), two views + toggle (Tasks 3-4), graceful shrink (Tasks 3-4), no-data semantics (Tasks 3-4), error placeholders unchanged (Tasks 3-4 keep the existing branches), composite heuristic removal (Task 3), verification (Task 5), CONTEXT.md (Task 5), out-of-scope items untouched.
- **Deviation from spec:** `PresentModeId` (int) in the DTO instead of a string — stated with rationale in Task 1; the spec's label table is implemented verbatim by `PresentMonPresentMode`.
- **Type consistency:** `GpuBusyPercent`/`DisplayedFps`/`DroppedFrames`/`GpuTimeMs`/`PresentModeId` appear identically in DTO (Task 1), record (Task 1), sample (Task 2), producer (Task 2), widget (Tasks 3-4), and all tests.
