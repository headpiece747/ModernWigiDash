using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Tests;

/// <summary>
/// The process-wide master owner: the vendor's slot 0 is ONE slot, so every
/// AIDA64 widget must borrow the same master (two masters would clear each
/// other's frame markers), and the last release stops it.
/// </summary>
[TestClass]
public sealed class AidaPanelServiceTests
{
    [TestMethod]
    public void Acquire_SharesOneMasterAndTheLastReleaseRecyclesIt()
    {
        var first = AidaPanelService.Acquire();
        var second = AidaPanelService.Acquire();

        Assert.AreSame(first, second, "two AIDA64 widgets must not both own slot 0");

        AidaPanelService.Release();
        AidaPanelService.Release();

        var third = AidaPanelService.Acquire();
        Assert.AreNotSame(first, third, "the last release disposes the shared master, so a later borrow gets a fresh one");

        AidaPanelService.Release();
    }
}
