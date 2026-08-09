using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class PresentMonFrameTimeProducerTests
{
    private sealed class FakePresentMonNative : IPresentMonNative
    {
        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public bool OpenSessionResult { get; set; } = true;
        public bool TrackProcessResult { get; set; } = true;
        public PresentMonDynamicSample? PollResult { get; set; }
        public PmStatus PollStatus { get; set; } = PmStatus.Success;
        public IReadOnlyList<double> FrameTimes { get; set; } = [];
        public Func<int, bool>? TrackHandler { get; set; }
        public Func<int, PresentMonPollResult>? PollHandler { get; set; }

        public int OpenSessionCalls { get; private set; }
        public int CloseSessionCalls { get; private set; }
        public bool Disposed { get; private set; }
        public List<int> TrackedProcessIds { get; } = [];
        public List<int> PolledProcessIds { get; } = [];

        public bool OpenSession()
        {
            OpenSessionCalls++;
            return OpenSessionResult;
        }

        public void CloseSession() => CloseSessionCalls++;

        public bool TrackProcess(int processId)
        {
            TrackedProcessIds.Add(processId);
            return TrackHandler is null ? TrackProcessResult : TrackHandler(processId);
        }

        public PresentMonPollResult PollDynamic(int processId)
        {
            PolledProcessIds.Add(processId);
            if (PollHandler is not null)
            {
                return PollHandler(processId);
            }
            return new PresentMonPollResult(
                PollStatus == PmStatus.Success ? PollResult : null, PollStatus);
        }

        public IReadOnlyList<double> DrainFrameTimes(int processId) => FrameTimes;

        public void Dispose() => Disposed = true;
    }

    private static FakePresentMonNative AvailableNative()
    {
        return new FakePresentMonNative
        {
            PollResult = new PresentMonDynamicSample(143.2, 110.4, 0.93, 4.05),
            FrameTimes = [6.5, 6.7],
        };
    }

    private static PresentMonFrameTimeProducer CreateProducer(
        FakePresentMonNative native,
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
        var native = new FakePresentMonNative
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
        var native = new FakePresentMonNative
        {
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
        Assert.AreEqual(0.93, dto.GpuBusyMs, 0.001, "GPU busy is already ms per frame (PM_METRIC_GPU_BUSY); no conversion");
        Assert.AreEqual(4.05, dto.CpuFrameTimeMs, 0.001);
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
            pid == 4322 ? new PresentMonDynamicSample(143.2, 110.4, 0.93, 4.05) : null,
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
        Assert.IsTrue(dto.IsAvailable, "the service is reachable � availability stays true");
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
            new PresentMonDynamicSample(120.0, 100.0, 0.5, 3.0), PmStatus.Success);

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
