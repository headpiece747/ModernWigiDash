using Microsoft.Win32;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

[TestClass]
public class PowerLifecycleTests
{
    private sealed class FakePowerModeSource : IPowerModeSource
    {
        public event Action<PowerModes>? ModeChanged;
        public bool Disposed { get; private set; }

        public void Raise(PowerModes mode) => ModeChanged?.Invoke(mode);

        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public void Suspend_RaisesOnSuspendActionOnly()
    {
        var source = new FakePowerModeSource();
        bool suspended = false;
        bool resumed = false;
        using var lifecycle = new PowerLifecycle(source,
            onSuspend: () => suspended = true,
            onResume: () => resumed = true);

        source.Raise(PowerModes.Suspend);

        Assert.IsTrue(suspended);
        Assert.IsFalse(resumed);
    }

    [TestMethod]
    public void Resume_RaisesOnResumeAction()
    {
        var source = new FakePowerModeSource();
        bool resumed = false;
        using var lifecycle = new PowerLifecycle(source,
            onSuspend: () => { },
            onResume: () => resumed = true);

        source.Raise(PowerModes.Resume);

        Assert.IsTrue(resumed);
    }

    [TestMethod]
    public void OtherModes_AreIgnored()
    {
        var source = new FakePowerModeSource();
        bool called = false;
        using var lifecycle = new PowerLifecycle(source,
            onSuspend: () => called = true,
            onResume: () => called = true);

        source.Raise(PowerModes.StatusChange);

        Assert.IsFalse(called);
    }

    [TestMethod]
    public void Dispose_UnsubscribesAndDisposesSource()
    {
        var source = new FakePowerModeSource();
        bool called = false;
        var lifecycle = new PowerLifecycle(source,
            onSuspend: () => called = true,
            onResume: () => called = true);

        lifecycle.Dispose();
        source.Raise(PowerModes.Suspend);

        Assert.IsFalse(called, "after dispose the lifecycle must not react");
        Assert.IsTrue(source.Disposed, "the source must be disposed with the lifecycle");
    }
}
