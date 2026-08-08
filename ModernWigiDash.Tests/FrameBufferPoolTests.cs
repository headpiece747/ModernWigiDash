using System.Threading.Channels;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class FrameBufferPoolTests
{
    [TestMethod]
    public void Acquire_ReturnsExactSizeBuffer()
    {
        var pool = new FrameBufferPool(bufferSize: 1202944, capacity: 2);

        byte[]? buffer = pool.Acquire();

        Assert.IsNotNull(buffer);
        Assert.AreEqual(1202944, buffer.Length);
    }

    [TestMethod]
    public void Acquire_ReturnsNull_WhenPoolExhausted()
    {
        var pool = new FrameBufferPool(bufferSize: 1024, capacity: 1);
        pool.Acquire();

        byte[]? second = pool.Acquire();

        Assert.IsNull(second, "Exhausted pool must hand out nothing (caller drops the frame)");
    }

    [TestMethod]
    public void Release_ThenAcquire_ReusesTheSameBuffer()
    {
        var pool = new FrameBufferPool(bufferSize: 1024, capacity: 1);
        byte[]? first = pool.Acquire();
        pool.Release(first!);

        byte[]? second = pool.Acquire();

        Assert.AreSame(first, second, "Released buffer must be reused, not re-allocated");
    }

    [TestMethod]
    public void Release_WrongSizeBuffer_IsIgnored()
    {
        var pool = new FrameBufferPool(bufferSize: 1024, capacity: 1);
        byte[]? first = pool.Acquire();
        Assert.IsNotNull(first, "The pool must hand out its only buffer before the wrong-size release");

        pool.Release(new byte[512]);

        byte[]? second = pool.Acquire();
        Assert.IsNull(second, "A wrong-size release must not enter the pool");
    }

    [TestMethod]
    public void FullCycle_MultipleFrames_KeepsAllocatingFromPool()
    {
        var pool = new FrameBufferPool(bufferSize: 1024, capacity: 4);
        HashSet<byte[]> seen = [];

        for (int i = 0; i < 10; i++)
        {
            byte[]? b = pool.Acquire();
            Assert.IsNotNull(b);
            seen.Add(b);
            pool.Release(b);
        }

        Assert.AreEqual(4, seen.Count, "The pool must never allocate more than its capacity");
    }

    [TestMethod]
    public void DrainToLatest_WithDroppedHook_ReturnsAllButLast()
    {
        var channel = Channel.CreateUnbounded<string>();
        List<string> dropped = [];
        channel.Writer.TryWrite("a");
        channel.Writer.TryWrite("b");
        channel.Writer.TryWrite("c");

        string? latest = ChannelFrameCoalescer.DrainToLatest(channel.Reader, dropped.Add);

        Assert.AreEqual("c", latest);
        CollectionAssert.AreEqual(new[] { "a", "b" }, dropped);
    }

    [TestMethod]
    public void DrainToLatest_WithDroppedHook_EmptyChannel_NoCallbacks()
    {
        var channel = Channel.CreateUnbounded<string>();
        List<string> dropped = [];

        string? latest = ChannelFrameCoalescer.DrainToLatest(channel.Reader, dropped.Add);

        Assert.IsNull(latest);
        Assert.AreEqual(0, dropped.Count);
    }
}
