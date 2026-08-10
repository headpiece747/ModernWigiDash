using System.Threading.Channels;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Single definition of the frame-coalescing policy used at every hop of the
/// frame pipeline: drain a DropOldest channel and keep only the most recent
/// frame, so stale frames are never replayed after a backlog.
/// </summary>
public static class ChannelFrameCoalescer
{
    /// <summary>
    /// Drains <paramref name="reader"/>, returning the latest element or the
    /// type's default when the channel was empty. Every dropped element is
    /// passed to <paramref name="onDropped"/> so pooled buffers can be
    /// returned. Unconstrained so both class (string) and struct (FrameSlot)
    /// element types can flow through the one coalescing definition; callers
    /// detect "empty" with a default check. An empty channel never invokes
    /// <paramref name="onDropped"/>.
    /// </summary>
    public static T DrainToLatest<T>(ChannelReader<T> reader, Action<T> onDropped)
    {
        T latest = default!;
        bool any = false;
        while (reader.TryRead(out var item))
        {
            if (any)
            {
                onDropped(latest);
            }
            latest = item;
            any = true;
        }
        return latest;
    }
}
