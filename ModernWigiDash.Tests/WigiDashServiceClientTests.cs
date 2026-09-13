using ModernWigiDash.Hardware.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class WigiDashServiceClientTests
{
    [TestMethod]
    public void TryConnect_VerifiesConnectionStateConsistency()
    {
        // The vendor service may or may not be running on this machine; the
        // invariant is that IsConnected reflects the TryConnect verdict.
        using var client = new WigiDashServiceClient();
        var connected = client.TryConnect();
        Assert.AreEqual(connected, client.IsConnected);
    }

    [TestMethod]
    public void Sensors_WhenNotConnected_IsReadyFalse()
    {
        using var client = new WigiDashServiceClient();
        Assert.IsFalse(client.Sensors.IsReady);
        Assert.IsNull(client.Sensors.GetSensorList());
        Assert.IsNull(client.Sensors.GetSensorValue(0, 0, 0));
    }

    [TestMethod]
    public void Aida_WhenNotConnected_IsReadyFalse()
    {
        using var client = new WigiDashServiceClient();
        Assert.IsFalse(client.Aida.IsReady);
        Assert.IsNull(client.Aida.ReadAidaMmap(0, 1024));
    }

    [TestMethod]
    public void Dispose_WhenNeverConnected_DoesNotThrow()
    {
        var client = new WigiDashServiceClient();
        client.Dispose();
        // Second dispose is a no-op; IsConnected must be false after dispose.
        Assert.IsFalse(client.IsConnected);
        client.Dispose();
    }

    [TestMethod]
    public void TryConnect_AfterDispose_ReturnsFalse()
    {
        var client = new WigiDashServiceClient();
        client.Dispose();
        Assert.IsFalse(client.TryConnect());
    }

    [TestMethod]
    public void VendorSensorItem_RecordShape_MatchesVendorContract()
    {
        var item = new VendorSensorItem(Guid.NewGuid(), "CPU", 0, 1, 2, "Temperature", "°C");
        Assert.AreEqual("CPU", item.Name);
        Assert.AreEqual(0, item.ReadingType);
        Assert.AreEqual(1, item.SensorId1);
        Assert.AreEqual(2, item.SensorId2);
        Assert.AreEqual("Temperature", item.Type);
        Assert.AreEqual("°C", item.Unit);
    }

    [TestMethod]
    public void VendorSensorReading_RecordShape_CarriesValueAndValidity()
    {
        var reading = new VendorSensorReading(42.5, true);
        Assert.AreEqual(42.5, reading.Value);
        Assert.IsTrue(reading.IsValid);

        var invalid = new VendorSensorReading(0, false);
        Assert.IsFalse(invalid.IsValid);
    }
}
