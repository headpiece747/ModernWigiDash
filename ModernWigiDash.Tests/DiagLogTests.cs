using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The <see cref="DiagLog"/> composition rule: LogCadence (first-log /
/// every-Nth) + the write sink, with the category tag baked in once at
/// construction. The injected write seam keeps the tests deterministic — no
/// dependency on the process-wide FileLog writer binding.
/// </summary>
[TestClass]
public class DiagLogTests
{
    [TestMethod]
    public void Write_WithLogFirst_FiresFirstAndEveryNthTaggedWithCategory()
    {
        List<string> lines = [];
        var diag = new DiagLog("USB-WINUSB", 3, logFirst: true, write: lines.Add);

        for (int i = 0; i < 12; i++)
            diag.Write($"line {i:000}");

        // logFirst fires on the very first call, then every 3rd: counts 1, 3,
        // 6, 9, 12 — calls 0, 2, 5, 8, 11. Every line carries the tag.
        CollectionAssert.AreEqual(
            new[]
            {
                "[USB-WINUSB] line 000",
                "[USB-WINUSB] line 002",
                "[USB-WINUSB] line 005",
                "[USB-WINUSB] line 008",
                "[USB-WINUSB] line 011",
            },
            lines);
    }

    [TestMethod]
    public void Write_WithoutLogFirst_FirstCallSilent()
    {
        List<string> lines = [];
        var diag = new DiagLog("USB-BULK-LIBUSB", 3, write: lines.Add);

        for (int i = 0; i < 12; i++)
            diag.Write($"line {i:000}");

        // Every-Nth cadence stays silent on the first call: counts 3, 6, 9, 12.
        CollectionAssert.AreEqual(
            new[]
            {
                "[USB-BULK-LIBUSB] line 002",
                "[USB-BULK-LIBUSB] line 005",
                "[USB-BULK-LIBUSB] line 008",
                "[USB-BULK-LIBUSB] line 011",
            },
            lines);
    }

    [TestMethod]
    public void Write_CadenceOne_FiresEveryCall()
    {
        List<string> lines = [];
        var diag = new DiagLog("USB-WINUSB", 1, write: lines.Add);

        for (int i = 0; i < 5; i++)
            diag.Write($"line {i:000}");

        Assert.AreEqual(5, lines.Count, "cadence 1 fires on every call");
        StringAssert.StartsWith(lines[0], "[USB-WINUSB] ");
        StringAssert.StartsWith(lines[4], "[USB-WINUSB] ");
    }
}
