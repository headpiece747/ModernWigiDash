using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class DisplayWireTests
{
    [TestMethod]
    public void Release_ConcurrentTransfers_NeverDisposesWhileTransferActiveOrRunsOnDisposedBackend()
    {
        using var wire = new DisplayWire();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int uafViolations = 0;
        int transfersExecuted = 0;

        var transferThread = Task.Run(() =>
        {
            byte[] buf = new byte[8];
            while (!cts.IsCancellationRequested && Volatile.Read(ref uafViolations) == 0)
            {
                wire.ControlOut(0x01, 0, buf);
                wire.ControlIn(0x02, 0, 0, buf, out _);
                wire.WriteBulkData(buf);
                wire.RunTracked(b => b?.ControlOut(0x03, 0, buf) ?? false);
            }
        });

        for (int i = 0; i < 400 && Volatile.Read(ref uafViolations) == 0; i++)
        {
            var backend = new UseAfterFreeProbeBackend(
                () => Interlocked.Increment(ref uafViolations),
                () => Interlocked.Increment(ref transfersExecuted));
            wire.AdoptLive(backend);
            Thread.SpinWait(50);
            wire.Release();
        }

        cts.Cancel();
        transferThread.GetAwaiter().GetResult();

        Assert.AreEqual(0, Volatile.Read(ref uafViolations),
            "Release must clear _backend BEFORE draining and disposing so no transfer can use a disposing or disposed native handle");
        Assert.IsGreaterThan(0, Volatile.Read(ref transfersExecuted),
            "concurrent transfers must actually execute against adopted backends rather than passing vacuously");
    }

    [TestMethod]
    public void AdoptAndAbandon_ConcurrentTransfers_DrainsDisplacedAndAbandonedBackendsSafely()
    {
        using var wire = new DisplayWire();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int uafViolations = 0;
        int transfersExecuted = 0;

        var transferThread = Task.Run(() =>
        {
            byte[] buf = new byte[8];
            while (!cts.IsCancellationRequested && Volatile.Read(ref uafViolations) == 0)
            {
                wire.ControlOut(0x01, 0, buf);
                wire.RunTracked(b => b?.ControlOut(0x02, 0, buf) ?? false);
            }
        });

        for (int i = 0; i < 250 && Volatile.Read(ref uafViolations) == 0; i++)
        {
            var first = new UseAfterFreeProbeBackend(
                () => Interlocked.Increment(ref uafViolations),
                () => Interlocked.Increment(ref transfersExecuted));
            var replacement = new UseAfterFreeProbeBackend(
                () => Interlocked.Increment(ref uafViolations),
                () => Interlocked.Increment(ref transfersExecuted));
            var unadoptedLocal = new UseAfterFreeProbeBackend(
                () => Interlocked.Increment(ref uafViolations),
                () => Interlocked.Increment(ref transfersExecuted));

            wire.Adopt(first);
            wire.Adopt(replacement);
            Assert.IsFalse(first.IsOpen, "Adopt must drain and dispose the displaced previous backend before publishing the replacement");
            Thread.SpinWait(40);
            wire.AbandonAttempt(unadoptedLocal);
            Assert.IsFalse(replacement.IsOpen, "AbandonAttempt must dispose the currently adopted backend");
            Assert.IsFalse(unadoptedLocal.IsOpen, "AbandonAttempt must dispose an unadopted local handle too");
        }

        cts.Cancel();
        transferThread.GetAwaiter().GetResult();

        Assert.AreEqual(0, Volatile.Read(ref uafViolations));
        Assert.IsGreaterThan(0, Volatile.Read(ref transfersExecuted));
    }

    [TestMethod]
    public void Dispose_RejectsSubsequentAdoptAndStopsTransfers()
    {
        using var wire = new DisplayWire();
        var live = new UseAfterFreeProbeBackend(static () => { });
        var lateAdopt = new UseAfterFreeProbeBackend(static () => { });
        var lateAdoptLive = new UseAfterFreeProbeBackend(static () => { });

        wire.AdoptLive(live);
        wire.Dispose();

        Assert.IsFalse(live.IsOpen, "Dispose must release any currently adopted backend");

        wire.Adopt(lateAdopt);
        wire.AdoptLive(lateAdoptLive);

        Assert.IsFalse(lateAdopt.IsOpen, "Adopt after Dispose must immediately dispose the rejected backend");
        Assert.IsFalse(lateAdoptLive.IsOpen, "AdoptLive after Dispose must immediately dispose the rejected backend");
        Assert.IsFalse(wire.IsConnected);
        Assert.IsFalse(wire.ControlOut(0x01, 0, []));
    }

    private sealed class UseAfterFreeProbeBackend(Action onViolation, Action? onTransfer = null) : ITransferBackend
    {
        private int _activeTransfers;
        private int _disposingOrDisposed;

        public bool IsOpen => Volatile.Read(ref _disposingOrDisposed) == 0;

        public bool ControlOut(byte request, ushort wValue, byte[]? data) => ProbeTransfer();

        public bool ControlIn(byte request, byte[] buffer, out int transferred, ushort wValue = 0, ushort wIndex = 0)
        {
            transferred = buffer.Length;
            return ProbeTransfer();
        }

        public bool BulkWrite(byte pipeId, byte[] data, out int transferred)
        {
            transferred = data.Length;
            return ProbeTransfer();
        }

        private bool ProbeTransfer()
        {
            Interlocked.Increment(ref _activeTransfers);
            onTransfer?.Invoke();
            try
            {
                if (Volatile.Read(ref _disposingOrDisposed) != 0)
                {
                    onViolation();
                    return false;
                }

                Thread.SpinWait(80);

                if (Volatile.Read(ref _disposingOrDisposed) != 0)
                {
                    onViolation();
                    return false;
                }

                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeTransfers);
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposingOrDisposed, 1);
            // Widen the window between Dispose starting and Release setting _backend = null.
            Thread.SpinWait(400);
            if (Volatile.Read(ref _activeTransfers) > 0)
            {
                onViolation();
            }
        }
    }
}
