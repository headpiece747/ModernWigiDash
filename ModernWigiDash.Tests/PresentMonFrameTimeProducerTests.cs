using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class PresentMonFrameTimeProducerTests
{
    private static StubPresentMonNative AvailableNative()
    {
        return new StubPresentMonNative
        {
            IsAvailable = true,
            OpenSessionResult = true,
            // GPU busy 4.0 ms at 143.2 fps → 4.0 * 143.2 / 10 = 57.28 % busy.
            PollResult = new PresentMonDynamicSample(143.2, 110.4, 4.0, 4.05, 142.8, 2, 6.1, 4),
            FrameTimes = [6.5, 6.7],
        };
    }

    private static PresentMonFrameTimeProducer CreateProducer(
        StubPresentMonNative native,
        int foregroundPid,
        Func<int, IReadOnlyList<int>>? childrenProvider = null,
        Func<int, string>? nameProvider = null)
    {
        return new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => foregroundPid, childrenProvider ?? (_ => [])),
            nameProvider ?? (_ => "game.exe"));
    }

    [TestMethod]
    public void Poll_NotAvailable_ReturnsUnavailableDtoWithReason()
    {
        var native = new StubPresentMonNative
        {
            IsAvailable = false,
            UnavailableReason = "PresentMonAPI2.dll not found",
        };
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsFalse(dto.IsAvailable);
        Assert.AreEqual("PresentMonAPI2.dll not found", dto.ErrorMessage);
        Assert.AreEqual(0, native.OpenSessionCalls, "must not attempt a session when the library is missing");
        Assert.AreEqual(0, native.TrackedProcessIds.Count);
    }

    [TestMethod]
    public void Poll_NoForegroundWindow_ReturnsIdleDtoWithoutTracking()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 0);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable, "no foreground window is not an availability failure");
        Assert.AreEqual(-1, dto.ProcessId, "idle state must signal monitor-refresh mode");
        Assert.AreEqual(0, dto.Fps);
        Assert.AreEqual(0, native.TrackedProcessIds.Count);
        Assert.AreEqual(0, native.OpenSessionCalls);
    }

    [TestMethod]
    public void Poll_SelfForeground_ReturnsIdleDto()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, Environment.ProcessId);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(-1, dto.ProcessId);
        Assert.AreEqual(0, native.TrackedProcessIds.Count);
    }

    [TestMethod]
    public void Poll_OpenSessionFails_ReturnsUnavailableDto()
    {
        var native = new StubPresentMonNative
        {
            IsAvailable = true,
            OpenSessionResult = false,
            UnavailableReason = "PresentMon service not running",
        };
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsFalse(dto.IsAvailable);
        Assert.AreEqual("PresentMon service not running", dto.ErrorMessage);
        Assert.AreEqual(1, native.OpenSessionCalls);
        Assert.AreEqual(0, native.TrackedProcessIds.Count);
    }

    [TestMethod]
    public void Poll_TracksProcessAndMapsSampleToDto()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(4321, dto.ProcessId);
        Assert.AreEqual("game.exe", dto.ProcessName);
        Assert.AreEqual(143.2, dto.Fps, 0.001);
        Assert.AreEqual(1000.0 / 143.2, dto.FrameTimeMs, 0.001);
        Assert.AreEqual(110.4, dto.Low1PercentFps, 0.001);
        Assert.AreEqual(4.0 * 143.2 / 10.0, dto.GpuBusyPercent, 0.001,
            "PM_METRIC_GPU_BUSY is ms per frame; the producer converts to the overlay-style busy-per-frame percent");
        Assert.AreEqual(4.05, dto.CpuFrameTimeMs, 0.001);
        Assert.AreEqual(142.8, dto.DisplayedFps, 0.001);
        Assert.AreEqual(2, dto.DroppedFrames);
        Assert.AreEqual(6.1, dto.GpuTimeMs, 0.001);
        Assert.AreEqual(4, dto.PresentModeId);
        double expectedLow01 = FrameTimeStatistics.Low01PercentFps([6.5, 6.7]);
        Assert.AreEqual(expectedLow01, dto.Low01PercentFps, 0.001);
        CollectionAssert.AreEqual(new[] { 6.5, 6.7 }, dto.RecentFrameTimesMs.ToArray());
        CollectionAssert.Contains(native.TrackedProcessIds, 4321);
        CollectionAssert.Contains(native.PolledProcessIds, 4321);
    }

    [TestMethod]
    public void Poll_PidChange_ReappliesTrackingOnNewPid()
    {
        int pid = 100;
        var native = AvailableNative();
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        producer.Poll();

        pid = 200;
        producer.Poll();

        CollectionAssert.AreEqual(new[] { 100, 200 }, native.TrackedProcessIds);
        Assert.AreEqual(1, native.OpenSessionCalls, "session must be opened once across polls");
    }

    [TestMethod]
    public void Poll_PidChange_StopsTrackingTheStaleTarget()
    {
        // The observed on-device failure: the game stayed tracked after
        // alt-tab, so the dynamic query kept returning the game's hidden
        // presents as every polled pid's data. The tracked set must mirror the
        // candidate set — the stale target is stopped when it leaves.
        int pid = 100;
        var native = AvailableNative();
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        producer.Poll();
        pid = 200;
        producer.Poll();

        CollectionAssert.Contains(native.StoppedProcessIds, 100,
            "the target that left the candidate set must be untracked");
        Assert.IsFalse(native.StoppedProcessIds.Contains(200),
            "the current target must stay tracked");
        Assert.AreEqual(1, native.OpenSessionCalls, "untracking must not touch the session");
    }

    [TestMethod]
    public void Poll_NoForeground_StopsAllTracking()
    {
        int pid = 100;
        var native = AvailableNative();
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        producer.Poll();
        pid = 0; // desktop / no foreground window
        var dto = producer.Poll();

        Assert.AreEqual(-1, dto.ProcessId);
        Assert.IsTrue(dto.IsAvailable);
        CollectionAssert.Contains(native.StoppedProcessIds, 100,
            "with no candidates nothing may stay tracked — a stale target would keep reporting forever");
    }

    [TestMethod]
    public void Poll_SamePidAcrossPolls_NeverStopsTracking()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        producer.Poll();
        producer.Poll();
        producer.Poll();

        Assert.AreEqual(0, native.StoppedProcessIds.Count,
            "a stable candidate set must never churn tracking");
    }

    // ── target-trust policy: foreground switches hold the zero state ──

    [TestMethod]
    public void Poll_ForegroundSwitch_HoldsZeroWhileSettling()
    {
        // The on-device failure: after the game loses the foreground,
        // PresentMon returns the game's frozen data for every polled pid — so
        // the new foreground's samples cannot be trusted immediately. The
        // settling window holds the zero state and only tracks (never polls).
        int pid = 100;
        var native = AvailableNative();
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        Assert.AreEqual(100, producer.Poll().ProcessId, "the game reports while foreground");

        pid = 200;
        var dto = producer.Poll();

        Assert.AreEqual(-1, dto.ProcessId, "the new foreground must not report while settling");
        Assert.IsTrue(dto.IsAvailable);
        Assert.IsTrue(dto.CaptureHealthy);
        Assert.IsFalse(native.PolledProcessIds.Contains(200), "the settling window must not poll the new target");
        Assert.IsTrue(native.TrackedProcessIds.Contains(200), "the new target is tracked so its data accumulates");
        Assert.IsTrue(native.StoppedProcessIds.Contains(100), "the departed target is untracked");
    }

    [TestMethod]
    public void Poll_ForegroundSwitch_AdoptsNewTargetAfterSettling()
    {
        int pid = 100;
        var native = AvailableNative();
        // The new target presents with its own data (per-pid sample), so once
        // adopted it differs from the departed target's values and reports.
        native.PollHandler = polledPid => new PresentMonPollResult(
            new PresentMonDynamicSample(
                polledPid == 100 ? 143.2 : 120.0, 110.4, 4.0, 4.05,
                polledPid == 100 ? 142.8 : 119.8, 2, 6.1, 4), PmStatus.Success);
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        producer.Poll(); // live 100
        pid = 200;

        Assert.AreEqual(-1, producer.Poll().ProcessId, "settling poll 1");
        Assert.AreEqual(-1, producer.Poll().ProcessId, "settling poll 2");
        var adopted = producer.Poll(); // streak 3 = AdoptAfterPolls → adoption poll

        Assert.AreEqual(200, adopted.ProcessId, "the new target is adopted after the settling window");
        Assert.AreEqual(120.0, adopted.Fps, 0.001);
    }

    [TestMethod]
    public void Poll_AdoptedTargetFrozenData_KeepsZeroUntilItDiffers()
    {
        // The frozen-data guard: the adopted target's first samples are still
        // the departed target's values; the zero state must persist until the
        // sample differs (the new target actually presents).
        int pid = 100;
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(
            new PresentMonDynamicSample(143.2, 110.4, 4.0, 4.05, 142.8, 2, 6.1, 4), PmStatus.Success);
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        Assert.AreEqual(100, producer.Poll().ProcessId, "the game reports while foreground");

        pid = 200;
        Assert.AreEqual(-1, producer.Poll().ProcessId, "settling poll 1");
        Assert.AreEqual(-1, producer.Poll().ProcessId, "settling poll 2");
        Assert.AreEqual(-1, producer.Poll().ProcessId,
            "the adoption poll still reads the departed target's frozen data → zero");

        native.PollHandler = _ => new PresentMonPollResult(
            new PresentMonDynamicSample(120.0, 100.0, 0.5, 3.0, 119.8, 0, 4.0, 8), PmStatus.Success);
        var live = producer.Poll();

        Assert.AreEqual(200, live.ProcessId, "once the sample differs, the adopted target reports");
        Assert.AreEqual(120.0, live.Fps, 0.001);
    }

    [TestMethod]
    public void Poll_ReturnToSameTarget_ReportsLiveAfterSettling()
    {
        int pid = 100;
        var native = AvailableNative();
        native.PollHandler = polledPid => new PresentMonPollResult(
            new PresentMonDynamicSample(
                polledPid == 100 ? 143.2 : 120.0, 110.4, 4.0, 4.05,
                polledPid == 100 ? 142.8 : 119.8, 2, 6.1, 4), PmStatus.Success);
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => pid, _ => []),
            _ => "game.exe");

        Assert.AreEqual(100, producer.Poll().ProcessId);

        pid = 200;
        Assert.AreEqual(-1, producer.Poll().ProcessId);
        Assert.AreEqual(-1, producer.Poll().ProcessId);
        Assert.AreEqual(200, producer.Poll().ProcessId, "the adopted target reports once its data differs");

        pid = 100; // back to the game
        Assert.AreEqual(-1, producer.Poll().ProcessId, "the returning game settles first");
        Assert.AreEqual(-1, producer.Poll().ProcessId);
        var back = producer.Poll();

        Assert.AreEqual(100, back.ProcessId, "the returning game reports after the settling window");
        Assert.AreEqual(143.2, back.Fps, 0.001);
    }

    [TestMethod]
    public void Poll_NoDataYet_ReturnsIdleDto()
    {
        var native = AvailableNative();
        native.PollResult = null;
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(-1, dto.ProcessId);
        Assert.AreEqual(4321, native.TrackedProcessIds[0], "tracking still applies even before data arrives");
    }

    [TestMethod]
    public void Poll_TrackProcessFailure_ReturnsIdleDto()
    {
        var native = AvailableNative();
        native.TrackProcessResult = false;
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(-1, dto.ProcessId);
        Assert.AreEqual(0, native.PolledProcessIds.Count, "must not poll a process tracking rejected");
    }

    [TestMethod]
    public void Poll_VideoPlayerTrackedButNoPresentData_DeclaresCaptureDeadAfterGrace()
    {
        // The video-present pattern: PresentMon tracks the player but never
        // returns present data (composition/overlay presents are not
        // attributed to the player). Pin the producer's response: Idle for
        // the grace window, then CaptureDead.
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = CreateProducer(native, 7777);

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < PresentMonFrameTimeProducer.CaptureHealthGracePolls; i++)
        {
            dto = producer.Poll();
        }

        Assert.IsTrue(dto!.IsAvailable, "A tracked-but-silent process stays 'available'");
        Assert.IsFalse(dto.CaptureHealthy, "After the grace window the capture is declared dead");
        Assert.AreEqual(-1, dto.ProcessId, "The dead-capture DTO reports no target process");
    }

    [TestMethod]
    public void Poll_VideoPlayerTrackedButNoPresentData_IdleDuringGrace()
    {
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = CreateProducer(native, 7777);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.IsTrue(dto.CaptureHealthy, "The grace window must not report capture dead yet");
        Assert.AreEqual(-1, dto.ProcessId, "Idle reports no process — the widget renders monitor mode");
    }

    [TestMethod]
    public void Poll_VideoPlayerPresentingAtLowFps_MapsFpsThrough()
    {
        // A 24fps video must report 23.97 through the DTO unchanged — nothing
        // in the pipeline may floor, clamp, or zero low frame rates.
        var native = AvailableNative();
        native.PollResult = new PresentMonDynamicSample(23.97, 22.1, 2.1, 1.2, 23.9, 1, 2.0, 4);
        var producer = CreateProducer(native, 7777);

        var dto = producer.Poll();

        Assert.AreEqual(23.97, dto.Fps, 0.001);
        Assert.AreEqual(7777, dto.ProcessId);
        Assert.IsTrue(dto.CaptureHealthy);
    }

    [TestMethod]
    public void Poll_RecentFrameTimes_CappedAt240KeepingLatest()
    {
        var native = AvailableNative();
        native.FrameTimes = Enumerable.Range(0, 1000).Select(i => i * 0.01).ToList();
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.AreEqual(240, dto.RecentFrameTimesMs.Count, "sparkline window must be capped at 240 samples");
        Assert.AreEqual(7.6, dto.RecentFrameTimesMs[0], 0.001, "must keep the newest 240 samples (760 * 0.01)");
        Assert.AreEqual(9.99, dto.RecentFrameTimesMs[^1], 0.001);
    }

    [TestMethod]
    public void Poll_OpenSessionOnce_ReappliesTrackingEachPoll()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        producer.Poll();
        producer.Poll();
        producer.Poll();

        Assert.AreEqual(1, native.OpenSessionCalls);
        CollectionAssert.AreEqual(new[] { 4321, 4321, 4321 }, native.TrackedProcessIds,
            "tracking is re-applied per poll; TrackProcess is idempotent at the native seam");
    }

    [TestMethod]
    public void Dispose_DisposesNativeSession()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        producer.Dispose();

        Assert.IsTrue(native.Disposed);
    }

    [TestMethod]
    public void Poll_SessionLost_ResetsSessionAndReturnsUnavailableDto()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        Assert.IsTrue(producer.Poll().IsAvailable);

        native.PollStatus = PmStatus.SessionNotOpen;
        var dto = producer.Poll();

        Assert.IsFalse(dto.IsAvailable, "a dead session must surface as unavailable, not idle");
        Assert.IsTrue(dto.ErrorMessage.Length > 0, "the transient loss must carry a message while reconnecting");
        Assert.AreEqual(1, native.CloseSessionCalls, "the dead session handle must be closed");
        Assert.AreEqual(1, native.OpenSessionCalls, "re-opening happens on the next tick, not in a tight loop");
    }

    [TestMethod]
    public void Poll_SessionLostThenServiceReturns_ReestablishesSessionAndData()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        Assert.IsTrue(producer.Poll().IsAvailable);

        native.PollStatus = PmStatus.SessionNotOpen;
        Assert.IsFalse(producer.Poll().IsAvailable);

        native.PollStatus = PmStatus.Success;
        var recovered = producer.Poll();

        Assert.IsTrue(recovered.IsAvailable, "the producer must recover once the service is back");
        Assert.AreEqual(4321, recovered.ProcessId);
        Assert.AreEqual(143.2, recovered.Fps, 0.001);
        Assert.AreEqual(2, native.OpenSessionCalls, "session must be re-opened after the service restart");
        CollectionAssert.AreEqual(new[] { 4321, 4321, 4321 }, native.TrackedProcessIds,
            "tracking is re-applied per poll on the fresh session (idempotent at the native seam)");
    }

    [TestMethod]
    public void Poll_NonSessionFailureStatus_DoesNotResetSession()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        producer.Poll();

        native.PollStatus = PmStatus.InvalidPid;
        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable, "a non-session failure is not a session loss");
        Assert.AreEqual(-1, dto.ProcessId);
        Assert.AreEqual(0, native.CloseSessionCalls, "the session must survive non-session failures");
        Assert.AreEqual(1, native.OpenSessionCalls);
    }

    [TestMethod]
    public void Poll_RootNoDataButDescendantHasData_ReportsDescendant()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321, pid => pid == 4321 ? [4322] : []);
        native.PollHandler = pid => new PresentMonPollResult(
            pid == 4322 ? new PresentMonDynamicSample(143.2, 110.4, 71.0, 4.05, 142.8, 2, 6.1, 4) : null,
            PmStatus.Success);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(4322, dto.ProcessId,
            "the reporting pid must be the descendant that actually presents");
        Assert.AreEqual("game.exe", dto.ProcessName);
        Assert.AreEqual(143.2, dto.Fps, 0.001);
        CollectionAssert.AreEqual(new[] { 4321, 4322 }, native.PolledProcessIds,
            "the whole tree must be polled in order until a sample arrives");
        CollectionAssert.AreEqual(new[] { 4321, 4322 }, native.TrackedProcessIds,
            "every candidate must be tracked so its data can arrive on a later tick");
    }

    [TestMethod]
    public void Poll_NoCandidateHasData_ReturnsIdle()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321, pid => pid == 4321 ? [4322] : []);
        native.PollResult = null;

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(-1, dto.ProcessId);
        CollectionAssert.AreEqual(new[] { 4321, 4322 }, native.PolledProcessIds,
            "every candidate must be polled before giving up");
        CollectionAssert.AreEqual(new[] { 4321, 4322 }, native.TrackedProcessIds);
    }

    [TestMethod]
    public void Poll_SessionLostMidLoop_ResetsAndStopsPolling()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321, pid => pid == 4321 ? [4322] : []);
        native.PollHandler = pid => new PresentMonPollResult(
            null, pid == 4321 ? PmStatus.SessionNotOpen : PmStatus.Success);

        var dto = producer.Poll();

        Assert.IsFalse(dto.IsAvailable, "a dead session mid-loop must surface as unavailable, not idle");
        Assert.AreEqual(1, native.CloseSessionCalls, "the dead session handle must be closed");
        CollectionAssert.AreEqual(new[] { 4321 }, native.PolledProcessIds,
            "polling must stop immediately on a session loss — no further candidates");
    }

    [TestMethod]
    public void Poll_OwnProcessInCandidates_Skipped()
    {
        int ownPid = Environment.ProcessId;
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321, pid => pid == 4321 ? [ownPid, 4322] : []);

        var dto = producer.Poll();

        Assert.AreEqual(4321, dto.ProcessId, "the own process must be skipped, the root still reports");
        CollectionAssert.DoesNotContain(native.PolledProcessIds, ownPid);
        CollectionAssert.DoesNotContain(native.TrackedProcessIds, ownPid);
    }

    [TestMethod]
    public void Poll_TrackFailureOnRoot_FallsThroughToDescendant()
    {
        var native = AvailableNative();
        native.TrackHandler = pid => pid != 4321;
        var producer = CreateProducer(native, 4321, pid => pid == 4321 ? [4322] : []);

        var dto = producer.Poll();

        Assert.AreEqual(4322, dto.ProcessId, "a candidate tracking rejected must be skipped, not fatal");
    }
    [TestMethod]
    public void Poll_TargetPresentingButNotDisplayed_ReturnsIdleZeroState()
    {
        // Backgrounded fullscreen games keep presenting while nothing of them
        // reaches the display (and often stay/re-grab the foreground, so the
        // resolver keeps returning them). DISPLAYED_FPS is the "is it actually
        // on screen" signal: 0 must read as the idle zero state, never as the
        // hidden present rate.
        var native = AvailableNative();
        native.PollResult = new PresentMonDynamicSample(143.2, 110.4, 4.0, 4.05, 0, 2, 6.1, 4); // DisplayedFps = 0
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsTrue(dto.IsAvailable);
        Assert.IsTrue(dto.CaptureHealthy, "the capture is fine — the target is simply not displayed");
        Assert.AreEqual(-1, dto.ProcessId, "not-displayed reads as the idle zero state");
        Assert.AreEqual(0, dto.Fps);
        Assert.AreEqual(0, dto.RecentFrameTimesMs.Count, "no frame times are buffered for a not-displayed target");
    }

    [TestMethod]
    public void Poll_NotDisplayedThenDisplayed_RecoversToLive()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 4321);

        native.PollResult = new PresentMonDynamicSample(143.2, 110.4, 4.0, 4.05, 0, 2, 6.1, 4);
        Assert.AreEqual(-1, producer.Poll().ProcessId, "backgrounded: idle zero state");

        native.PollResult = new PresentMonDynamicSample(143.2, 110.4, 4.0, 4.05, 142.8, 2, 6.1, 4);
        var dto = producer.Poll();

        Assert.AreEqual(4321, dto.ProcessId, "back in the game: live again");
        Assert.AreEqual(143.2, dto.Fps, 0.001);
        Assert.IsTrue(dto.CaptureHealthy);
    }

    [TestMethod]
    public void Poll_NotDisplayedAcrossPolls_NeverFlagsCaptureDead()
    {
        var native = AvailableNative();
        native.PollResult = new PresentMonDynamicSample(143.2, 110.4, 4.0, 4.05, 0, 2, 6.1, 4);
        var producer = CreateProducer(native, 4321);

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < PresentMonFrameTimeProducer.CaptureHealthGracePolls + 5; i++)
        {
            dto = producer.Poll();
        }

        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.IsAvailable);
        Assert.IsTrue(dto.CaptureHealthy,
            "a backgrounded target is not a dead capture — the zero state must persist, not decay into 'capture inactive'");
        Assert.AreEqual(-1, dto.ProcessId);
        Assert.AreEqual(0, dto.Fps);
    }

    [TestMethod]
    public void Poll_NoDataForGracePeriod_FlagsCaptureUnhealthy()
    {
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = CreateProducer(native, 4321);

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < 10; i++)
        {
            dto = producer.Poll();
        }

        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.IsAvailable, "the service is reachable — availability stays true");
        Assert.IsFalse(dto.CaptureHealthy, "no present data for the whole grace window must flag the capture");
        StringAssert.Contains(dto.ErrorMessage, "not producing present data");
        Assert.AreEqual(-1, dto.ProcessId);
    }

    [TestMethod]
    public void Poll_BeforeGracePeriod_StaysIdleHealthy()
    {
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = CreateProducer(native, 4321);

        var dto = producer.Poll();

        Assert.IsTrue(dto.CaptureHealthy, "a few empty polls are normal startup/static-window behavior");
        Assert.AreEqual(-1, dto.ProcessId);
    }

    [TestMethod]
    public void Poll_DataArrivesAfterUnhealthy_Recovers()
    {
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = CreateProducer(native, 4321);

        for (int i = 0; i < 10; i++)
        {
            producer.Poll();
        }

        native.PollHandler = _ => new PresentMonPollResult(
            new PresentMonDynamicSample(120.0, 100.0, 0.5, 3.0, 119.8, 0, 4.0, 8), PmStatus.Success);

        var dto = producer.Poll();

        Assert.IsTrue(dto.CaptureHealthy, "a real sample must restore the healthy state");
        Assert.AreEqual(120.0, dto.Fps, 0.001);
        Assert.AreEqual(4321, dto.ProcessId);
    }

    [TestMethod]
    public void Poll_IdleWithNoCandidates_DoesNotCountTowardUnhealthy()
    {
        var native = AvailableNative();
        var producer = CreateProducer(native, 0); // no foreground window

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < 20; i++)
        {
            dto = producer.Poll();
        }

        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.CaptureHealthy, "desktop/own-window idle must never flag the capture");
    }

    [TestMethod]
    public void Poll_IdleBetweenTargets_ResetsGraceCounter()
    {
        int foregroundPid = 4321;
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => foregroundPid, _ => []),
            _ => "game.exe");

        for (int i = 0; i < 5; i++)
        {
            Assert.IsTrue(producer.Poll().CaptureHealthy, "startup empty polls must be healthy");
        }

        foregroundPid = 0; // target closes — idle between targets
        for (int i = 0; i < 3; i++)
        {
            Assert.IsTrue(producer.Poll().CaptureHealthy, "idle polls must not count toward the grace window");
        }

        foregroundPid = 4321; // new target appears
        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < 9; i++)
        {
            dto = producer.Poll();
            Assert.IsNotNull(dto);
            Assert.IsTrue(dto.CaptureHealthy, $"poll {i + 1} after the idle gap must still be healthy");
        }

        dto = producer.Poll();
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.CaptureHealthy,
            "the full grace window must re-apply after an idle gap, not a leaked remainder of the previous target");
    }

    [TestMethod]
    public void Poll_AllCandidatesTrackRejected_ReturnsIdleWithoutCounting()
    {
        var native = AvailableNative();
        native.TrackHandler = _ => false;
        var producer = CreateProducer(native, 4321, pid => pid == 4321 ? [4322, 4323] : []);

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < 15; i++)
        {
            dto = producer.Poll();
        }

        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.IsAvailable);
        Assert.AreEqual(-1, dto.ProcessId, "an unwatchable target set is an idle-style outcome");
        Assert.IsTrue(dto.CaptureHealthy, "track rejections must never count toward a dead capture");
        Assert.AreEqual(0, native.PolledProcessIds.Count, "nothing is polled when every track attempt is rejected");
    }

    [TestMethod]
    public void Poll_TrackRejectedCandidate_NotCountedAsEmptyData()
    {
        IReadOnlyList<int> children = [];
        var native = AvailableNative();
        native.TrackHandler = _ => false; // every candidate rejected
        var producer = new PresentMonFrameTimeProducer(
            native,
            new TrackedTargetResolver(() => 4321, _ => children),
            _ => "game.exe");

        for (int i = 0; i < 5; i++)
        {
            Assert.IsTrue(producer.Poll().CaptureHealthy,
                $"poll {i + 1}: a fully rejected candidate set must never consume the grace window");
        }

        children = [4322]; // a second candidate appears that tracking accepts
        native.TrackHandler = pid => pid != 4321;
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < 9; i++)
        {
            dto = producer.Poll();
            Assert.IsNotNull(dto);
            Assert.IsTrue(dto.CaptureHealthy,
                $"poll {i + 1}: only the tracked polls count — 9 of them are inside the grace window");
        }

        dto = producer.Poll();
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.CaptureHealthy,
            "exactly the tracked-empty polls count — the rejected candidate must not shorten the grace");
        CollectionAssert.DoesNotContain(native.PolledProcessIds, 4321, "a rejected candidate is never polled");
    }

    [TestMethod]
    public void Poll_CaptureDeadBoundary_HealthyAtNineEmptyPollsDeadAtTen()
    {
        var native = AvailableNative();
        native.PollHandler = _ => new PresentMonPollResult(null, PmStatus.Success);
        var producer = CreateProducer(native, 4321);

        FrameTimeSnapshotDto? dto = null;
        for (int i = 0; i < 9; i++)
        {
            dto = producer.Poll();
        }

        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.CaptureHealthy, "9 tracked-but-empty polls are inside the grace window");

        dto = producer.Poll();
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.CaptureHealthy, "the 10th tracked-but-empty poll crosses the boundary");
    }
}
