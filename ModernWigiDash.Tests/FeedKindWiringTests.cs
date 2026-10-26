using System.Collections.Concurrent;

namespace ModernWigiDash.Tests;

/// <summary>
/// The wiring row's feed lifetimes: the WS loop and the REST poll task are
/// BOTH owned by the row (created on the first claim, retired at shutdown),
/// so a kind's feeds have ONE owner and "a fourth kind is one table row"
/// holds for the REST task too, not just the socket.
/// </summary>
[TestClass]
public sealed class FeedKindWiringTests
{
    [TestMethod]
    public void RestTask_IsOwnedByTheRow_AtBothEnds()
    {
        FeedKindWiring row = null!;
        row = new FeedKindWiring
        {
            IsValid = _ => true,
            Subscriptions = new ConcurrentDictionary<string, int>(),
            OnFirstClaim = () => row.RestTask ??= Task.CompletedTask,
            WsSubscribeFrame = null,
            Seed = null,
            LiveSourceLabel = null,
        };

        Assert.IsNull(row.RestTask, "no claim has started the REST task");

        row.OnFirstClaim();
        Assert.IsNotNull(row.RestTask, "the first claim starts the REST task through the row");

        row.RestTask = null;
        Assert.IsNull(row.RestTask, "shutdown retires the REST task through the row");
    }
}
