using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// The LibUsb chunked-write policy — previously only reachable through a real
/// USB device, now pure over a write delegate: chunk sizing, short-write
/// advance, mid-way failure, and completion accounting.
/// </summary>
[TestClass]
public class ChunkedBulkWriteTests
{
    private sealed class RecordingWriter
    {
        public List<(int Offset, int Size)> Calls = [];
        public Queue<(bool Ok, int Transferred, string ErrorDetail)> Results = [];

        public (bool Ok, int Transferred, string ErrorDetail) Write(int offset, int size)
        {
            Calls.Add((offset, size));
            return Results.Count > 0 ? Results.Dequeue() : (true, size, string.Empty);
        }
    }

    [TestMethod]
    public void Write_ExactChunkSize_SingleChunk()
    {
        var writer = new RecordingWriter();
        byte[] data = new byte[ChunkedBulkWrite.ChunkSize];

        bool ok = ChunkedBulkWrite.Write(data, writer.Write, out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(data.Length, transferred);
        Assert.AreEqual(1, writer.Calls.Count);
        Assert.AreEqual((0, ChunkedBulkWrite.ChunkSize), writer.Calls[0]);
    }

    [TestMethod]
    public void Write_LargerThanOneChunk_SplitsByChunkSize()
    {
        var writer = new RecordingWriter();
        byte[] data = new byte[ChunkedBulkWrite.ChunkSize + 1];

        bool ok = ChunkedBulkWrite.Write(data, writer.Write, out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(data.Length, transferred);
        Assert.AreEqual(2, writer.Calls.Count);
        Assert.AreEqual((0, ChunkedBulkWrite.ChunkSize), writer.Calls[0]);
        Assert.AreEqual((ChunkedBulkWrite.ChunkSize, 1), writer.Calls[1], "the last chunk carries the remainder");
    }

    [TestMethod]
    public void Write_ShortFirstChunk_NextChunkStartsAtTransferredLength()
    {
        var writer = new RecordingWriter();
        writer.Results.Enqueue((true, 100, string.Empty)); // short write: 100 of 262144
        byte[] data = new byte[ChunkedBulkWrite.ChunkSize + 1];

        bool ok = ChunkedBulkWrite.Write(data, writer.Write, out int transferred);

        Assert.IsTrue(ok, "the policy continues from the short write's true position");
        Assert.AreEqual(data.Length, transferred);
        Assert.AreEqual((0, ChunkedBulkWrite.ChunkSize), writer.Calls[0]);
        Assert.AreEqual((100, 262045), writer.Calls[1], "no gap skipped — offset advances by 100, and the last chunk carries the remainder");
    }

    [TestMethod]
    public void Write_MidwayChunkFailure_StopsAndReportsPartial()
    {
        var writer = new RecordingWriter();
        writer.Results.Enqueue((true, ChunkedBulkWrite.ChunkSize, string.Empty));
        writer.Results.Enqueue((false, 0, "PipeError"));
        byte[] data = new byte[ChunkedBulkWrite.ChunkSize * 2];
        List<string> logs = [];

        bool ok = ChunkedBulkWrite.Write(data, writer.Write, out int transferred, logs.Add);

        Assert.IsFalse(ok);
        Assert.AreEqual(ChunkedBulkWrite.ChunkSize, transferred, "the partial progress is reported");
        Assert.AreEqual(2, writer.Calls.Count, "no further chunks after the failure");
        Assert.AreEqual(1, logs.Count);
        StringAssert.Contains(logs[0], "PipeError");
    }

    [TestMethod]
    public void Write_ZeroByteWrite_FailsWithoutProgress()
    {
        var writer = new RecordingWriter();
        writer.Results.Enqueue((true, 0, string.Empty));

        bool ok = ChunkedBulkWrite.Write(new byte[10], writer.Write, out int transferred);

        Assert.IsFalse(ok, "a zero-progress chunk must not loop forever");
        Assert.AreEqual(0, transferred);
    }

    [TestMethod]
    public void Write_EmptyPayload_SucceedsWithNothing()
    {
        var writer = new RecordingWriter();

        bool ok = ChunkedBulkWrite.Write([], writer.Write, out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, transferred);
        Assert.AreEqual(0, writer.Calls.Count);
    }
}
