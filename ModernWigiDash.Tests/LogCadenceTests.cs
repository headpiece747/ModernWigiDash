using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>The diagnostic log-cadence rule — one tested rule for the five
/// hand-rolled modulo counters it replaced.</summary>
[TestClass]
public class LogCadenceTests
{
    [TestMethod]
    public void Due_EveryNth_FirstCallIsSilent()
    {
        var cadence = new LogCadence(60);

        Assert.IsFalse(cadence.Due(), "Every-Nth cadence must not fire on the first call");
        for (int i = 0; i < 58; i++)
        {
            Assert.IsFalse(cadence.Due());
        }
        Assert.IsTrue(cadence.Due(), "Every-Nth cadence must fire on the 60th call");
        Assert.IsFalse(cadence.Due(), "Every-Nth cadence must stay silent between cadence points");
        for (int i = 0; i < 58; i++)
        {
            Assert.IsFalse(cadence.Due());
        }
        Assert.IsTrue(cadence.Due(), "Every-Nth cadence must fire on the 120th call");
    }

    [TestMethod]
    public void Due_WithLogFirst_FiresOnFirstAndEveryNth()
    {
        var cadence = new LogCadence(60, logFirst: true);

        Assert.IsTrue(cadence.Due(), "logFirst cadence must fire on the first call");
        for (int i = 0; i < 58; i++)
        {
            Assert.IsFalse(cadence.Due());
        }
        Assert.IsTrue(cadence.Due(), "logFirst cadence must fire on the 60th call");
    }

    [TestMethod]
    public void Due_IntervalOne_FiresEveryCall()
    {
        var cadence = new LogCadence(1);

        Assert.IsTrue(cadence.Due());
        Assert.IsTrue(cadence.Due());
    }
}
