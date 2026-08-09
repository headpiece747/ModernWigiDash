using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

[TestClass]
public class TrackedTargetResolverTests
{
    private static TrackedTargetResolver CreateResolver(
        Func<int> foregroundPidProvider,
        Func<int, IReadOnlyList<int>> childrenProvider) =>
        new(foregroundPidProvider, childrenProvider);

    [TestMethod]
    public void GetForegroundProcessId_DelegatesToProvider()
    {
        var resolver = CreateResolver(() => 4242, _ => []);

        Assert.AreEqual(4242, resolver.GetForegroundProcessId());
    }

    [TestMethod]
    public void ResolveCandidates_RootOnly_ReturnsRoot()
    {
        var resolver = CreateResolver(() => 4242, _ => []);

        CollectionAssert.AreEqual(new[] { 4242 }, resolver.ResolveCandidates().ToArray());
    }

    [TestMethod]
    public void ResolveCandidates_RootWithChild_ReturnsRootFirstThenChild()
    {
        var resolver = CreateResolver(() => 4242, pid => pid == 4242 ? [4243] : []);

        CollectionAssert.AreEqual(new[] { 4242, 4243 }, resolver.ResolveCandidates().ToArray());
    }

    [TestMethod]
    public void ResolveCandidates_DeepTree_ReturnsRootFirstInStableBfsOrder()
    {
        // Full binary tree: children of p are 2p and 2p+1. BFS from root 1
        // yields 1,2,3,4,5,6,7 — root first, descendants in discovery order.
        var resolver = CreateResolver(() => 1, pid => pid <= 3 ? [pid * 2, pid * 2 + 1] : []);

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }, resolver.ResolveCandidates().ToArray());
    }

    [TestMethod]
    public void ResolveCandidates_UnboundedTree_CappedAt32Processes()
    {
        // Every pid spawns two children — the walk must stop at the cap.
        var resolver = CreateResolver(() => 1, pid => [pid * 2, pid * 2 + 1]);

        var candidates = resolver.ResolveCandidates();

        Assert.AreEqual(TrackedTargetResolver.MaxCandidateProcesses, candidates.Count);
        Assert.AreEqual(1, candidates[0], "the root must always be first");
        Assert.AreEqual(candidates.Count, candidates.Distinct().Count(), "the bounded walk must not revisit pids");
    }

    [TestMethod]
    public void ResolveCandidates_NoForegroundWindow_ReturnsEmpty()
    {
        var resolver = CreateResolver(() => 0, _ => []);

        Assert.AreEqual(0, resolver.ResolveCandidates().Count);
    }

    [TestMethod]
    public void ResolveCandidates_OwnProcess_ReturnsEmpty()
    {
        var resolver = CreateResolver(() => Environment.ProcessId, _ => []);

        Assert.AreEqual(0, resolver.ResolveCandidates().Count);
    }

    [TestMethod]
    public void ResolveCandidates_ChildCyclingBackToRoot_DoesNotRevisit()
    {
        var resolver = CreateResolver(() => 1, pid => pid == 1 ? [2] : [1]);

        CollectionAssert.AreEqual(new[] { 1, 2 }, resolver.ResolveCandidates().ToArray());
    }
}
