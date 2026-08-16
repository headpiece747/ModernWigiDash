using System.IO;
using System.Text;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the one bounded-read core both weather read legs (HTTP + disk cache)
/// adapt over: stream up to a cap, chunked via the array pool, returning the
/// buffered bytes. The per-leg GUARDS (declared-length pre-check, growth
/// detection) stay in the adapters and are pinned by the client/geocoder
/// tests; this file pins the shared chunking/limit loop itself.
/// </summary>
[TestClass]
public class BoundedReadTests
{
    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public async Task ReadAsync_BodyUnderCap_ReturnsAllBytes()
    {
        byte[] body = await BoundedRead.ReadAsync(StreamOf("hello"), cap: 1024, CancellationToken.None);

        Assert.AreEqual("hello", Encoding.UTF8.GetString(body));
    }

    [TestMethod]
    public async Task ReadAsync_BodyOverCap_TruncatesAtCap()
    {
        byte[] body = await BoundedRead.ReadAsync(StreamOf("abcdef"), cap: 3, CancellationToken.None);

        Assert.AreEqual("abc", Encoding.UTF8.GetString(body),
            "the read loop must stop at the cap instead of buffering the whole stream");
    }

    [TestMethod]
    public async Task ReadAsync_BodyExactlyAtCap_ReturnsFullBody()
    {
        byte[] body = await BoundedRead.ReadAsync(StreamOf("abc"), cap: 3, CancellationToken.None);

        Assert.AreEqual("abc", Encoding.UTF8.GetString(body),
            "a body exactly at the cap is a complete read, not a truncation");
    }

    [TestMethod]
    public async Task ReadAsync_EmptyStream_ReturnsEmptyBytes()
    {
        byte[] body = await BoundedRead.ReadAsync(StreamOf(""), cap: 1024, CancellationToken.None);

        Assert.AreEqual(0, body.Length);
    }

    [TestMethod]
    public async Task ReadAsync_LargerThanArrayPoolChunk_IsAssembledCorrectly()
    {
        // Crosses the 81920-byte pooled chunk boundary: the assembly must be
        // exact across multiple pooled reads (no gap, no duplication).
        string payload = new string('x', 200_000);
        byte[] body = await BoundedRead.ReadAsync(StreamOf(payload), cap: 200_000, CancellationToken.None);

        Assert.AreEqual(payload, Encoding.UTF8.GetString(body));
    }

    [TestMethod]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => BoundedRead.ReadAsync(StreamOf("hello"), cap: 1024, cts.Token));
    }
}
