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
    /// Drains <paramref name="reader"/>, returning the latest element or null
    /// when the channel was empty.
    /// </summary>
    public static T? DrainToLatest<T>(ChannelReader<T> reader) where T : class
    {
        T? latest = null;
        while (reader.TryRead(out var item))
        {
            latest = item;
        }
        return latest;
    }
}
