using System.Collections.Concurrent;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Pool of fixed-size byte buffers for the frame pipeline.
///
/// The 30 FPS render tick used to allocate a fresh ~1.2 MB array per frame;
/// those arrays land on the large-object heap and drove ~36 MB/s of LOH churn
/// and a sustained gen-2 GC storm. This pool keeps a small ring of exact-size
/// buffers instead: the render tick acquires, the frame sender releases.
/// Buffers are exact-size (no ArrayPool slack) because the display's RGB565
/// framebuffer payload is fixed-size — every buffer matches the encoder's
/// output exactly, so nothing is reallocated between acquire and release.
/// </summary>
public sealed class FrameBufferPool
{
    private readonly ConcurrentQueue<byte[]> _free = new();
    private readonly int _capacity;
    private int _freeCount;

    /// <summary>Exact size of every pooled buffer, in bytes.</summary>
    public int BufferSize { get; }

    /// <param name="bufferSize">Exact buffer size in bytes.</param>
    /// <param name="capacity">Number of buffers to pre-allocate (in-flight maximum + margin).</param>
    public FrameBufferPool(int bufferSize, int capacity)
    {
        BufferSize = bufferSize;
        _capacity = capacity;
        for (int i = 0; i < capacity; i++)
        {
            _free.Enqueue(new byte[bufferSize]);
        }
        _freeCount = capacity;
    }

    /// <summary>
    /// Rents a buffer of <see cref="BufferSize"/> bytes, or null when the pool
    /// is exhausted (caller drops the frame — matches DropOldest under load).
    /// </summary>
    public byte[]? Acquire()
    {
        if (!_free.TryDequeue(out var buffer)) return null;
        Interlocked.Decrement(ref _freeCount);
        return buffer;
    }

    /// <summary>
    /// Returns a buffer to the pool. Buffers of the wrong size are ignored; a
    /// release past the pool's capacity is dropped (double-release guard — the
    /// pool never grows beyond what the constructor pre-allocated).
    /// </summary>
    public void Release(byte[] buffer)
    {
        if (buffer.Length != BufferSize) return;
        if (Interlocked.Increment(ref _freeCount) > _capacity)
        {
            Interlocked.Decrement(ref _freeCount);
            return;
        }
        _free.Enqueue(buffer);
    }
}
