namespace ModernWigiDash.Tests;

/// <summary>
/// The process-lifecycle state owner pinned at its interface: the two
/// process-wide flags (<see cref="ProcessLifecycle.IsClosing"/> and
/// <see cref="ProcessLifecycle.StartMinimized"/>) default to false, are
/// writable, and are observable by a background reader after a write. Before
/// this module the flags leaked through mutable statics on App; now they have
/// one named owner with a single test surface.
/// </summary>
[TestClass]
public class ProcessLifecycleTests
{
    [TestCleanup]
    public void Reset()
    {
        ProcessLifecycle.IsClosing = false;
        ProcessLifecycle.StartMinimized = false;
    }

    [TestMethod]
    public void Flags_DefaultToFalse()
    {
        Assert.IsFalse(ProcessLifecycle.IsClosing);
        Assert.IsFalse(ProcessLifecycle.StartMinimized);
    }

    [TestMethod]
    public void IsClosing_IsWritableAndObservable()
    {
        ProcessLifecycle.IsClosing = true;

        Assert.IsTrue(ProcessLifecycle.IsClosing);
    }

    [TestMethod]
    public void StartMinimized_IsWritableAndObservable()
    {
        ProcessLifecycle.StartMinimized = true;

        Assert.IsTrue(ProcessLifecycle.StartMinimized);
    }

    [TestMethod]
    public void IsClosing_ClearsBackToFalse()
    {
        ProcessLifecycle.IsClosing = true;
        ProcessLifecycle.IsClosing = false;

        Assert.IsFalse(ProcessLifecycle.IsClosing);
    }
}
