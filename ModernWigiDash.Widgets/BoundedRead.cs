using System.Buffers;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The one bounded-read core behind every byte-limited read in the weather
/// cluster: streams up to <paramref name="cap"/> bytes from any readable
/// stream into a MemoryStream, chunked via the shared array pool, returning
/// the buffered bytes. The HTTP leg (<see cref="WeatherGeocoder.ReadBoundedAsync"/>)
/// and the disk-cache leg (the WeatherClient cache load) are adapters that
/// apply their own guards (declared-length pre-check, growth-after-read
/// detection) on top of this core — one chunking/limit implementation
/// instead of two hand-rolled loops that could drift.
/// </summary>
internal static class BoundedRead
{
    /// <summary>The chunk size for the pooled reads — large enough to make the
    /// pooled-buffer traffic negligible, small enough to bound the rented
    /// array. The same size the two replaced loops shared.</summary>
    private const int ChunkSize = 81920;

    public static async Task<byte[]> ReadAsync(Stream stream, long cap, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)Math.Min(cap, int.MaxValue));
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long total = 0;
            while (total < cap)
            {
                int remaining = (int)Math.Min(chunk.Length, cap - total);
                int read = await stream.ReadAsync(chunk.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
