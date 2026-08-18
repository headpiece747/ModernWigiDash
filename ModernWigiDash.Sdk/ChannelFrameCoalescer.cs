using System.Threading.Channels;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The frame-coalescing policy: drain a DropOldest channel and keep only the
/// most recent element, so stale elements are never replayed after a
/// backlog. FrameDelivery's bounded slot channel is the production consumer
/// (drained by its sender loop, every dropped slot's pooled buffer returned);
/// the rule is unconstrained so any channel element shape reuses the one
/// definition.
/// </summary>
public static class ChannelFrameCoalescer
{
    /// <summary>
    /// Drains <paramref name="reader"/>, returning the latest element or the
    /// type's default when the channel was empty. Every dropped element is
    /// passed to <paramref name="onDropped"/> so pooled buffers can be
    /// returned. Unconstrained: any element shape can reuse the rule
    /// (FrameDelivery's <c>FrameSlot</c> in production; the string usage is
    /// the behavior-pinning test only); callers detect "empty" with a default
    /// check. An empty channel never invokes <paramref name="onDropped"/>.
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
