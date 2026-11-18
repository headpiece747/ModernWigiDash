using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The vendor-sensor refresh core pinned directly: the empty-pick fallback,
/// the unknown-key no-reading, the invalid-reading degrade, the empty-list
/// no-reading, the 250&nbsp;ms read throttle, and the 5&nbsp;s list cadence —
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
        public List<(int Rt, int Id1, int Id2)> RequestedValueKeys { get; } = [];
        public IReadOnlyList<VendorSensorItem>? List { get; set; }
        public VendorSensorReading? Value { get; set; }
        public Func<int, int, int, VendorSensorReading?>? ValueByKey { get; set; }
        public VendorSensorStatus Status { get; set; } = VendorSensorStatus.Ready;

        public bool IsReady => Status == VendorSensorStatus.Ready;

        public IReadOnlyList<VendorSensorItem>? GetSensorList()
        {
            ListCalls++;
            return List;
        }

        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2)
        {
            ValueCalls++;
            RequestedValueKeys.Add((readingType, sensorId1, sensorId2));
            return ValueByKey is not null ? ValueByKey(readingType, sensorId1, sensorId2) : Value;
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

        tick += 260;
        refresh.Refresh(sensors, "0:1:0");

        Assert.AreEqual(valueCallsAfterFirst + 1, sensors.ValueCalls, "a read past the 250 ms window re-reads the sensor");
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

    [TestMethod]
    public void Refresh_WhenStatusBecomesNotReady_InvalidatesCachedListWithoutThrashingOnNullReads()
    {
        long tick = 0;
        var sensors = new FakeSensors
        {
            List = [Sensor(0, 1, 0, "CPU", "C")],
            Value = new VendorSensorReading(45.0, true),
        };
        var refresh = new VendorSensorRefresh(() => tick);

        Assert.IsNotNull(refresh.Refresh(sensors, "0:1:0"));
        Assert.IsTrue(refresh.HasSensorList);
        int listCallsBeforeNull = sensors.ListCalls;

        // Cold read or per-key fault returning null while Status == Ready must NOT clear _sensorList.
        sensors.Value = null;
        tick += 260;
        Assert.IsNull(refresh.Refresh(sensors, "0:1:0"));
        Assert.IsTrue(refresh.HasSensorList, "null value read on Ready service must not drop HasSensorList");
        Assert.AreEqual(listCallsBeforeNull, sensors.ListCalls);

        // Status dropping out of Ready invalidates _sensorList immediately.
        sensors.Status = VendorSensorStatus.Disconnected;
        tick += 33;
        Assert.IsNull(refresh.Refresh(sensors, "0:1:0"));
        Assert.IsFalse(refresh.HasSensorList);

        // Recovery must reload immediately without waiting 5 s.
        sensors.Status = VendorSensorStatus.Ready;
        sensors.Value = new VendorSensorReading(47.0, true);
        tick += 33;
        var recovered = refresh.Refresh(sensors, "0:1:0");
        Assert.IsNotNull(recovered);
        Assert.IsTrue(refresh.HasSensorList);
        Assert.AreEqual(47.0, recovered.Value);
    }

    [TestMethod]
    public void Refresh_AutomaticWhenFirstSensorInvalid_SelectsAndLatchesFirstValidSensor()
    {
        long tick = 0;
        var sensors = new FakeSensors
        {
            List =
            [
                Sensor(0, 0, 0, "Header", ""),
                Sensor(0, 1, 0, "Offline", "C"),
                Sensor(0, 2, 0, "Core", "C"),
            ],
            ValueByKey = (_, id1, _) => id1 == 2
                ? new VendorSensorReading(55.0, true)
                : new VendorSensorReading(0, false),
        };
        var refresh = new VendorSensorRefresh(() => tick);

        var first = refresh.Refresh(sensors, "");
        Assert.IsNotNull(first);
        Assert.AreEqual("Core", first.Name);
        Assert.AreEqual(55.0, first.Value);

        sensors.RequestedValueKeys.Clear();
        tick += 260;
        var second = refresh.Refresh(sensors, "");
        Assert.IsNotNull(second);
        Assert.AreEqual(1, sensors.RequestedValueKeys.Count, "once latched, Automatic must query only the settled key");
        Assert.AreEqual((0, 2, 0), sensors.RequestedValueKeys[0]);

        // If the latched sensor later returns null (e.g., per-key FaultException), Automatic
        // must clear its latch and recover onto another valid candidate on the next cycle.
        sensors.ValueByKey = (_, id1, _) => id1 switch
        {
            2 => null,
            1 => new VendorSensorReading(41.0, true),
            _ => new VendorSensorReading(0, false),
        };
        tick += 260;
        Assert.IsNull(refresh.Refresh(sensors, ""));
        tick += 260;
        var reselected = refresh.Refresh(sensors, "");
        Assert.IsNotNull(reselected);
        Assert.AreEqual("Offline", reselected.Name);
        Assert.AreEqual(41.0, reselected.Value);
    }
}
