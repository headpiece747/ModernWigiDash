using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The vendor-sensor refresh core pinned directly: the empty-pick fallback,
/// the unknown-key no-reading, the invalid-reading degrade, the empty-list
/// no-reading, the 500&nbsp;ms read throttle, and the 5&nbsp;s list cadence —
/// all drivable through a fake <see cref="ISensorValues"/> + a fake tick, with
/// no widget, no WPF surface, and no <see cref="VendorService"/> static.
/// </summary>
[TestClass]
public class VendorSensorRefreshTests
{
    private static VendorSensorItem Sensor(int rt, int id1, int id2, string name, string unit)
        => new(Guid.NewGuid(), name, rt, id1, id2, null, unit);

    private sealed class FakeSensors : ISensorValues
    {
        public int ListCalls { get; private set; }
        public int ValueCalls { get; private set; }
        public IReadOnlyList<VendorSensorItem>? List { get; set; }
        public VendorSensorReading? Value { get; set; }

        public bool IsReady => true;

        public IReadOnlyList<VendorSensorItem>? GetSensorList()
        {
            ListCalls++;
            return List;
        }

        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2)
        {
            ValueCalls++;
            return Value;
        }
    }

    [TestMethod]
    public void Refresh_EmptyPick_FallsBackToTheFirstSensor()
    {
        var sensors = new FakeSensors { List = [Sensor(3, 7, 2, "GPU", "C")], Value = new VendorSensorReading(60.0, true) };
        var refresh = new VendorSensorRefresh(() => 0);

        var reading = refresh.Refresh(sensors, "");

        Assert.IsNotNull(reading, "an empty pick falls back to the first sensor");
        Assert.AreEqual("GPU", reading.Name);
        Assert.AreEqual(60.0, reading.Value);
    }

    [TestMethod]
    public void Refresh_UnknownKey_ReturnsNull()
    {
        var sensors = new FakeSensors { List = [Sensor(3, 7, 2, "GPU", "C")], Value = new VendorSensorReading(60.0, true) };
        var refresh = new VendorSensorRefresh(() => 0);

        var reading = refresh.Refresh(sensors, "9:9:9");

        Assert.IsNull(reading, "a key not in the list yields no reading");
    }

    [TestMethod]
    public void Refresh_InvalidReading_ReturnsNull()
    {
        var sensors = new FakeSensors { List = [Sensor(0, 1, 0, "CPU", "C")], Value = new VendorSensorReading(45.0, false) };
        var refresh = new VendorSensorRefresh(() => 0);

        var reading = refresh.Refresh(sensors, "0:1:0");

        Assert.IsNull(reading, "an invalid reading degrades to no reading");
    }

    [TestMethod]
    public void Refresh_EmptyList_ReturnsNull()
    {
        var sensors = new FakeSensors { List = [], Value = new VendorSensorReading(45.0, true) };
        var refresh = new VendorSensorRefresh(() => 0);

        var reading = refresh.Refresh(sensors, "0:1:0");

        Assert.IsNull(reading, "an empty sensor list yields no reading");
    }

    [TestMethod]
    public void Refresh_ThrottlesRepeatedReadsWithinTheWindow()
    {
        long tick = 0;
        var sensors = new FakeSensors { List = [Sensor(0, 1, 0, "CPU", "C")], Value = new VendorSensorReading(45.0, true) };
        var refresh = new VendorSensorRefresh(() => tick);

        SensorReading? first = refresh.Refresh(sensors, "0:1:0");
        Assert.IsNotNull(first, "a valid reading is returned");
        int valueCallsAfterFirst = sensors.ValueCalls;

        tick += 100;
        SensorReading? second = refresh.Refresh(sensors, "0:1:0");

        Assert.IsNotNull(second, "a throttled read still returns the held reading");
        Assert.AreEqual(first, second, "a throttled read returns the same reading without re-reading the sensor");
        Assert.AreEqual(valueCallsAfterFirst, sensors.ValueCalls, "a throttled read does not re-read the sensor");
    }

    [TestMethod]
    public void Refresh_ReReadsTheValueOnceTheThrottleWindowElapses()
    {
        long tick = 0;
        var sensors = new FakeSensors { List = [Sensor(0, 1, 0, "CPU", "C")], Value = new VendorSensorReading(45.0, true) };
        var refresh = new VendorSensorRefresh(() => tick);

        refresh.Refresh(sensors, "0:1:0");
        int valueCallsAfterFirst = sensors.ValueCalls;

        tick += 600;
        refresh.Refresh(sensors, "0:1:0");

        Assert.AreEqual(valueCallsAfterFirst + 1, sensors.ValueCalls, "a read past the 500 ms window re-reads the sensor");
    }

    [TestMethod]
    public void Refresh_RefetchesTheListOnlyOnTheSettledCadence()
    {
        long tick = 0;
        var sensors = new FakeSensors { List = [Sensor(0, 1, 0, "CPU", "C")], Value = new VendorSensorReading(45.0, true) };
        var refresh = new VendorSensorRefresh(() => tick);

        refresh.Refresh(sensors, "0:1:0");
        Assert.AreEqual(1, sensors.ListCalls, "the first read fetches the list");

        tick += 1000;
        refresh.Refresh(sensors, "0:1:0");
        Assert.AreEqual(1, sensors.ListCalls, "the list is not refetched within its 5 s cadence");

        tick += 5000;
        refresh.Refresh(sensors, "0:1:0");
        Assert.AreEqual(2, sensors.ListCalls, "the list is refetched once its 5 s cadence elapses");
    }
}
