using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Tests;

/// <summary>
/// The process-wide master owner: the vendor's slot 0 is ONE slot, so every
/// AIDA64 widget must borrow the same master (two masters would clear each
/// other's frame markers), and the last release stops it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AidaPanelServiceTests
{
    [TestMethod]
    public void Acquire_SharesOneMasterAndTheLastReleaseRecyclesIt()
    {
        int outstandingBorrows = 0;
        var first = AidaPanelService.Acquire();
        outstandingBorrows++;
        var second = AidaPanelService.Acquire();
        outstandingBorrows++;
        try
        {
            Assert.AreSame(first, second, "two AIDA64 widgets must not both own slot 0");

            AidaPanelService.Release();
            outstandingBorrows--;
            AidaPanelService.Release();
            outstandingBorrows--;

            var third = AidaPanelService.Acquire();
            outstandingBorrows++;
            Assert.AreNotSame(first, third, "the last release disposes the shared master, so a later borrow gets a fresh one");
        }
        finally
        {
            while (outstandingBorrows-- > 0)
            {
                AidaPanelService.Release();
            }
        }
    }

    [TestMethod]
    public async Task Acquire_WhilePreviousReleaseIsSlow_ReturnsWithoutBlockingAndSerializesSlot0()
    {
        var firstSource = new HoldDisposeSource();
        var firstWriter = new RecordingInitWriter();
        var secondWriter = new RecordingInitWriter();
        var secondLogs = new List<string>();
        bool HasWaitingLog()
        {
            lock (secondLogs)
            {
                return secondLogs.Any(static line => line.Contains("waiting for previous panel master to release slot 0", StringComparison.Ordinal));
            }
        }
        void RecordSecondLog(string line)
        {
            lock (secondLogs) secondLogs.Add(line);
        }

        var first = AidaPanelService.Acquire(_ => new AidaPanelMaster(firstSource, firstWriter, log: static _ => { }));
        Task releaseTask = Task.CompletedTask;
        try
        {
            first.Start();
            await TestWait.WaitUntilAsync(() => firstWriter.InitCalls > 0, TimeSpan.FromSeconds(5));

            releaseTask = Task.Factory.StartNew(
                AidaPanelService.Release,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.IsTrue(firstSource.DisposeEntered.Wait(TimeSpan.FromSeconds(10)),
                "background release must begin disposing the first master");

            // Rapid re-add on UI thread must not stall behind the in-flight Dispose
            // (firstSource.ReleaseDispose remains unset until after Acquire finishes).
            AidaPanelMaster? second = null;
            var acquireThread = new Thread(() =>
            {
                second = AidaPanelService.Acquire(priorRelease =>
                    new AidaPanelMaster(new FakeAidaMmapSource(), secondWriter, priorRelease, log: RecordSecondLog));
            })
            {
                IsBackground = true
            };
            acquireThread.Start();
            Assert.IsTrue(acquireThread.Join(TimeSpan.FromSeconds(10)),
                "Acquire blocked while previous master was still releasing");
            Assert.IsNotNull(second);

            second.Start();
            await TestWait.WaitUntilAsync(HasWaitingLog, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, secondWriter.InitCalls,
                "the second master must wait off-thread until the first master finishes releasing slot 0");

            firstSource.ReleaseDispose.Set();
            await releaseTask;
            await TestWait.WaitUntilAsync(() => secondWriter.InitCalls > 0, TimeSpan.FromSeconds(5));
        }
        finally
        {
            firstSource.ReleaseDispose.Set();
            await releaseTask;
            AidaPanelService.Release();
            firstSource.DisposeEntered.Dispose();
            firstSource.ReleaseDispose.Dispose();
        }
    }

    [TestMethod]
    public async Task Acquire_WhileOldMasterSlotWorkOutlivesDispose_DelaysNextMasterRegistration()
    {
        var blockingWriter = new BlockingSlotWriter();
        var secondWriter = new RecordingInitWriter();
        var secondLogs = new List<string>();
        bool HasWaitingLog()
        {
            lock (secondLogs)
            {
                return secondLogs.Any(static line => line.Contains("waiting for previous panel master to release slot 0", StringComparison.Ordinal));
            }
        }
        void RecordSecondLog(string line)
        {
            lock (secondLogs) secondLogs.Add(line);
        }

        var first = AidaPanelService.Acquire(_ => new AidaPanelMaster(new FakeAidaMmapSource(), blockingWriter, log: static _ => { }));
        Task<bool> inFlightInit = Task.Run(first.EnsureRegistered);
        bool releasedFirst = false;
        bool acquiredSecond = false;
        try
        {
            Assert.IsTrue(blockingWriter.InitEntered.Wait(TimeSpan.FromSeconds(5)),
                "first master must enter slot-0 provider init");

            // Dispose finishes immediately (first loop was not running), yet slot-0
            // work is still inside TryInit and must continue holding _activeRelease.
            AidaPanelService.Release();
            releasedFirst = true;

            var second = AidaPanelService.Acquire(priorRelease =>
                new AidaPanelMaster(new FakeAidaMmapSource(), secondWriter, priorRelease, log: RecordSecondLog));
            acquiredSecond = true;

            second.Start();
            await TestWait.WaitUntilAsync(HasWaitingLog, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, secondWriter.InitCalls,
                "next master must wait while the old master's in-flight WCF slot-0 call is still unwinding after Dispose");

            blockingWriter.ReleaseInit.Set();
            Assert.IsFalse(await inFlightInit, "disposed first master must abort its registration once TryInit returns");

            await TestWait.WaitUntilAsync(() => secondWriter.InitCalls > 0, TimeSpan.FromSeconds(5));
        }
        finally
        {
            blockingWriter.ReleaseInit.Set();
            await inFlightInit;
            if (!releasedFirst || acquiredSecond)
            {
                AidaPanelService.Release();
            }

            blockingWriter.Dispose();
        }
    }

    private sealed class HoldDisposeSource : IAidaMmapSource
    {
        public ManualResetEventSlim DisposeEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseDispose { get; } = new(false);

        public bool TryRead(int offset, int length, byte[] destination, out string? error)
        {
            error = "not ready";
            return false;
        }

        public void Dispose()
        {
            DisposeEntered.Set();
            ReleaseDispose.Wait(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class RecordingInitWriter : IAidaMmapWriter
    {
        private int _initCalls;
        public int InitCalls => Volatile.Read(ref _initCalls);

        public bool TryInit(out string? error)
        {
            Interlocked.Increment(ref _initCalls);
            error = null;
            return true;
        }

        public bool TryWrite(int offset, byte[] data, out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class BlockingSlotWriter : IAidaMmapWriter, IDisposable
    {
        public ManualResetEventSlim InitEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseInit { get; } = new(false);

        public bool TryInit(out string? error)
        {
            InitEntered.Set();
            ReleaseInit.Wait(TimeSpan.FromSeconds(30));
            error = null;
            return true;
        }

        public bool TryWrite(int offset, byte[] data, out string? error)
        {
            error = null;
            return true;
        }

        public void Dispose()
        {
            InitEntered.Dispose();
            ReleaseInit.Dispose();
        }
    }
}
