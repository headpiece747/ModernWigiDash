using LibUsbDotNet;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives <see cref="LibUsbTransferBackend"/> through a fully-opened
/// <see cref="FakeLibUsbDevice"/> (the deviceProvider seam): the vendor control
/// in/out transfers, the chunked bulk write (success / short-write / error /
/// exception legs), and the dispose teardown are pinned without hardware.
/// </summary>
[TestClass]
public sealed class LibUsbTransferBackendTests
{
    private static LibUsbTransferBackend OpenedBackend(FakeLibUsbDevice device)
    {
        var backend = LibUsbTransferBackend.TryOpen(() => device);
        Assert.IsNotNull(backend, "a default-configured fake device must open cleanly");
        return backend;
    }

    [TestMethod]
    public void TryOpen_FullyConfiguredDevice_ReturnsAnOpenBackend()
    {
        var device = new FakeLibUsbDevice();

        using var backend = OpenedBackend(device);

        Assert.IsTrue(backend.IsOpen);
    }

    [TestMethod]
    public void ControlOut_Success_ReturnsTrue()
    {
        var device = new FakeLibUsbDevice();
        using var backend = OpenedBackend(device);

        bool ok = backend.ControlOut(DisplayProtocolConstants.CmdFrameHeader, 0, []);

        Assert.IsTrue(ok);
    }

    [TestMethod]
    public void ControlOut_WithPayload_ReturnsTrue()
    {
        var device = new FakeLibUsbDevice();
        using var backend = OpenedBackend(device);

        bool ok = backend.ControlOut(DisplayProtocolConstants.CmdFrameHeader, 0x1234, [1, 2, 3]);

        Assert.IsTrue(ok);
    }

    [TestMethod]
    public void ControlOut_NegativeTransferred_ReturnsFalse()
    {
        // A control OUT reporting a negative length is a failed transfer.
        var device = new FakeLibUsbDevice { ControlOutTransferred = -1 };
        using var backend = OpenedBackend(device);

        bool ok = backend.ControlOut(DisplayProtocolConstants.CmdFrameHeader, 0, []);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void ControlOut_DeviceThrows_IsCaughtAndReturnsFalse()
    {
        var device = new FakeLibUsbDevice { ControlOutException = new InvalidOperationException("fake ctrl fault") };
        using var backend = OpenedBackend(device);

        bool ok = backend.ControlOut(DisplayProtocolConstants.CmdFrameHeader, 0, []);

        Assert.IsFalse(ok, "a throwing control OUT must be caught and reported as a failure");
    }

    [TestMethod]
    public void ControlIn_Success_FillsTheBufferAndReportsTransferred()
    {
        var device = new FakeLibUsbDevice();
        using var backend = OpenedBackend(device);
        byte[] buffer = new byte[8];

        bool ok = backend.ControlIn(DisplayProtocolConstants.CmdGetTouch, buffer, out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(8, transferred, "a successful control IN fills the requested buffer");
    }

    [TestMethod]
    public void ControlIn_ZeroTransferred_ReturnsFalse()
    {
        var device = new FakeLibUsbDevice { ControlInTransferred = 0 };
        using var backend = OpenedBackend(device);
        byte[] buffer = new byte[8];

        bool ok = backend.ControlIn(DisplayProtocolConstants.CmdGetTouch, buffer, out int transferred);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, transferred);
    }

    [TestMethod]
    public void ControlIn_DeviceThrows_IsCaughtAndReturnsFalse()
    {
        var device = new FakeLibUsbDevice { ControlInException = new InvalidOperationException("fake ctrl-in fault") };
        using var backend = OpenedBackend(device);
        byte[] buffer = new byte[8];

        bool ok = backend.ControlIn(DisplayProtocolConstants.CmdGetTouch, buffer, out int transferred);

        Assert.IsFalse(ok, "a throwing control IN must be caught and reported as a failure");
        Assert.AreEqual(0, transferred);
    }

    [TestMethod]
    public void BulkWrite_SmallPayload_WritesOneChunkAndSucceeds()
    {
        var device = new FakeLibUsbDevice();
        using var backend = OpenedBackend(device);
        byte[] payload = new byte[64];

        bool ok = backend.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, payload, out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(64, transferred);
        Assert.AreEqual(1, device.WriteChunkCalls, "a sub-chunk-size payload is one chunk");
    }

    [TestMethod]
    public void BulkWrite_MultiChunkPayload_WritesAllChunksAndSucceeds()
    {
        // A payload larger than the chunk size is split into bounded chunks;
        // every chunk must be written and the total transferred must equal the
        // full payload length.
        int chunkSize = ChunkedBulkWrite.ChunkSize;
        var device = new FakeLibUsbDevice();
        using var backend = OpenedBackend(device);
        byte[] payload = new byte[chunkSize * 3 + 17];

        bool ok = backend.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, payload, out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(payload.Length, transferred);
        Assert.AreEqual(4, device.WriteChunkCalls, "three full chunks + a tail chunk");
    }

    [TestMethod]
    public void BulkWrite_ChunkError_IsReportedAsFailure()
    {
        // A chunk that returns a non-success Error stops the write and reports
        // a failure (the partial transfer count is what reached the wire).
        var device = new FakeLibUsbDevice { WriteChunkError = Error.Pipe };
        using var backend = OpenedBackend(device);
        byte[] payload = new byte[64];

        bool ok = backend.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, payload, out int transferred);

        Assert.IsFalse(ok, "a chunk error is a failed write");
        Assert.AreEqual(0, transferred, "no bytes are reported when the first chunk errors");
    }

    [TestMethod]
    public void BulkWrite_ShortChunk_TransfersOnlyWhatReachedTheWire()
    {
        // A chunk that writes fewer bytes than requested is a short write: the
        // chunked policy stops and reports the actual transferred count.
        var device = new FakeLibUsbDevice { WriteChunkTransferred = 10 };
        using var backend = OpenedBackend(device);
        byte[] payload = new byte[64];

        bool ok = backend.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, payload, out int transferred);

        Assert.IsFalse(ok, "a short chunk is a failed write — the caller routes it to abort");
        Assert.AreEqual(10, transferred, "only the bytes that reached the wire are reported");
    }

    [TestMethod]
    public void BulkWrite_WriterThrows_IsCaughtAndReturnsFalse()
    {
        var device = new FakeLibUsbDevice { WriteChunkException = new InvalidOperationException("fake bulk fault") };
        using var backend = OpenedBackend(device);
        byte[] payload = new byte[64];

        bool ok = backend.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, payload, out int transferred);

        Assert.IsFalse(ok, "a throwing writer must be caught and reported as a failure");
        Assert.AreEqual(0, transferred);
    }

    [TestMethod]
    public void Dispose_OpenBackend_ReleasesInterfaceAndCloses()
    {
        var device = new FakeLibUsbDevice();
        var backend = OpenedBackend(device);

        backend.Dispose();

        Assert.AreEqual(1, device.CloseCalls, "dispose must close the device");
    }

    [TestMethod]
    public void Dispose_AlreadyClosed_NoOp()
    {
        // A backend whose device is already closed must not attempt a second
        // release (the IsOpen guard).
        var device = new FakeLibUsbDevice();
        var backend = OpenedBackend(device);
        device.Close(); // force the closed state before dispose

        backend.Dispose();

        Assert.AreEqual(1, device.CloseCalls, "dispose on an already-closed device is a no-op");
    }

    [TestMethod]
    public void TryOpen_ProviderReturnsNull_ReturnsNull()
    {
        // An explicit provider that finds nothing is the same "no device"
        // verdict as the production default.
        LibUsbTransferBackend? backend = LibUsbTransferBackend.TryOpen(() => null);

        Assert.IsNull(backend);
    }

    [TestMethod]
    public void TryOpen_SetConfigurationFails_ContinuesAndOpens()
    {
        // SetConfiguration failure is non-fatal (the legacy driver tolerates it):
        // the leg logs and continues to claim + endpoint discovery, so the
        // backend still opens.
        var device = new FakeLibUsbDevice { SetConfigurationThrows = true };

        using var backend = OpenedBackend(device);

        Assert.IsTrue(backend.IsOpen, "a set-configuration fault must not abort the open");
    }

    [TestMethod]
    public void Dispose_CloseThrows_IsCaught()
    {
        // A close that throws during dispose (device already pulled) must be
        // caught by the teardown's exception leg, not propagate out of Dispose.
        var device = new FakeLibUsbDevice();
        var backend = OpenedBackend(device);
        device.CloseThrows = true; // arm the fault after the backend is adopted

        // Must not throw.
        backend.Dispose();
    }
}
