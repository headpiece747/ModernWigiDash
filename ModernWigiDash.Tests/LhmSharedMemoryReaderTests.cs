using System.IO;
using System.Text.Json;
using MessagePack;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.Sdk;
using IndexEntry = ModernWigiDash.App.LibreHardwareService.LhmSharedMemoryReader.IndexEntry;
using SensorBlock = ModernWigiDash.App.LibreHardwareService.LhmSharedMemoryReader.SensorBlock;

namespace ModernWigiDash.Tests;

[TestClass]
public class LhmSharedMemoryReaderTests
{
    // ── synthetic-map fixture: mirrors the LibreHardwareService writer ──
    // (MemoryMappedSensors.writeSensors: MetadataSize=12 → metadataBlockSize=16,
    //  indexOffset = msb + 4 + 36*4, dataOffset = indexOffset + indexLen + 4,
    //  index entry offsets relative to the start of the data blob, each sensor
    //  JSON null-terminated.)

    private const int MetaDataSize = 12; // sizeof(int) + sizeof(long) in LHS Metadata
    private const int MetadataBlockSize = 4 + MetaDataSize; // 16 — index/data fields start here
    private const int JsonIndexFormat = 1;
    private const int MessagePackIndexFormat = 2;
    private const long UnixNow = 1_752_000_000; // fixed for deterministic LastUpdate asserts

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly SensorBlock CpuTemp = new(
        Identifier: "/amdcpu/0/temperature/0",
        Name: "CPU Package",
        SensorType: "Temperature",
        HardwareName: "AMD Ryzen 7 5800X",
        HardwareType: "CPU",
        Value: 55.5,
        Min: 40.1,
        Max: 90.2);

    private static readonly SensorBlock GpuFan = new(
        Identifier: "/nvidiagpu/0/fan/0",
        Name: "GPU Fan",
        SensorType: "Fan",
        HardwareName: "NVIDIA GeForce RTX 3080",
        HardwareType: "Gpu",
        Value: 1200,
        Min: 0,
        Max: 3000);

    [TestMethod]
    public void TryParse_JsonIndexWithTwoSensors_MapsAllFields()
    {
        byte[] map = BuildMap(JsonIndexFormat, CpuTemp, GpuFan);

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.IsTrue(dto.IsConnected);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(UnixNow).UtcDateTime, dto.LastUpdate);
        Assert.AreEqual(2, dto.Readings.Count);

        SensorReadingDto cpu = dto.Readings[0];
        Assert.AreEqual("/amdcpu/0/temperature/0", cpu.SensorId);
        Assert.AreEqual("CPU Package", cpu.SensorName);
        Assert.AreEqual("AMD Ryzen 7 5800X", cpu.HardwareName);
        Assert.AreEqual("CPU", cpu.HardwareType);
        Assert.AreEqual("Temperature", cpu.SensorType);
        Assert.AreEqual("°C", cpu.Unit);
        Assert.AreEqual(55.5, cpu.Value);
        Assert.AreEqual(40.1, cpu.Min);
        Assert.AreEqual(90.2, cpu.Max);

        SensorReadingDto fan = dto.Readings[1];
        Assert.AreEqual("/nvidiagpu/0/fan/0", fan.SensorId);
        Assert.AreEqual("GPU Fan", fan.SensorName);
        Assert.AreEqual("NVIDIA GeForce RTX 3080", fan.HardwareName);
        Assert.AreEqual("Gpu", fan.HardwareType);
        Assert.AreEqual("Fan", fan.SensorType);
        Assert.AreEqual("RPM", fan.Unit);
        Assert.AreEqual(1200, fan.Value);
        Assert.AreEqual(0, fan.Min);
        Assert.AreEqual(3000, fan.Max);
    }

    [TestMethod]
    public void TryParse_MessagePackIndex_MapsSensors()
    {
        byte[] map = BuildMap(MessagePackIndexFormat, CpuTemp, GpuFan);

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.IsTrue(dto.IsConnected);
        Assert.AreEqual(2, dto.Readings.Count);
        Assert.AreEqual("/amdcpu/0/temperature/0", dto.Readings[0].SensorId);
        Assert.AreEqual("CPU Package", dto.Readings[0].SensorName);
        Assert.AreEqual("AMD Ryzen 7 5800X", dto.Readings[0].HardwareName);
        Assert.AreEqual("CPU", dto.Readings[0].HardwareType);
        Assert.AreEqual("Temperature", dto.Readings[0].SensorType);
        Assert.AreEqual("°C", dto.Readings[0].Unit);
        Assert.AreEqual(55.5, dto.Readings[0].Value);
        Assert.AreEqual("/nvidiagpu/0/fan/0", dto.Readings[1].SensorId);
        Assert.AreEqual("GPU Fan", dto.Readings[1].SensorName);
        Assert.AreEqual("RPM", dto.Readings[1].Unit);
        Assert.AreEqual(1200, dto.Readings[1].Value);
    }

    [TestMethod]
    public void TryParse_SensorIdPreserved()
    {
        var sensor = CpuTemp with { Identifier = "/amd cpu/0/Temperature#0 Core 1" };
        byte[] map = BuildMap(JsonIndexFormat, sensor);

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.AreEqual("/amd cpu/0/Temperature#0 Core 1", dto.Readings[0].SensorId,
            "SensorId must match the LHS identifier verbatim — it is the widget's stable machine key");
    }

    [TestMethod]
    public void TryParse_TruncatedHeader_ReturnsDisconnected()
    {
        byte[] map = new byte[40]; // shorter than the 52-byte fixed header block

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual(0, dto.Readings.Count);
    }

    [TestMethod]
    public void TryParse_TruncatedData_ReturnsDisconnected()
    {
        byte[] map = BuildMap(JsonIndexFormat, CpuTemp, GpuFan);
        WriteInt(map, MetadataBlockSize + 16, int.MaxValue / 2); // data-offset points past the map

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual(0, dto.Readings.Count);
    }

    [TestMethod]
    public void TryParse_EmptyMap_ReturnsDisconnected()
    {
        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse([]);

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual(0, dto.Readings.Count);
    }

    [TestMethod]
    public void TryParse_IndexExceedingMaxSensorEntries_ReturnsDisconnected()
    {
        List<IndexEntry> entries = [];
        for (int i = 0; i <= LhmSharedMemoryReader.MaxSensorEntries; i++)
        {
            entries.Add(new IndexEntry
            {
                Identifier = $"/sensor/{i}",
                Offset = 0,
                Size = 0,
                SensorName = $"Sensor {i}",
                SensorType = "Temperature",
                HardwareName = "CPU",
            });
        }

        byte[] map = BuildMap(JsonIndexFormat, entries, []);

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual(0, dto.Readings.Count);
    }

    [TestMethod]
    public void TryParse_EntryBlockSizeExceedingMaxSensorBlockBytes_ReturnsDisconnected()
    {
        List<IndexEntry> entries =
        [
            new()
            {
                Identifier = "/cpu/0/temperature/oversized",
                Offset = 0,
                Size = LhmSharedMemoryReader.MaxSensorBlockBytes + 1,
                SensorName = "Oversized Block",
                SensorType = "Temperature",
                HardwareName = "CPU",
            },
        ];
        byte[] data = new byte[LhmSharedMemoryReader.MaxSensorBlockBytes + 1];

        byte[] map = BuildMap(JsonIndexFormat, entries, data);

        SensorSnapshotDto dto = LhmSharedMemoryReader.TryParse(map);

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual(0, dto.Readings.Count);
    }

    [TestMethod]
    public void UnitFor_SensorTypeStrings_ReturnsExpectedUnits()
    {
        Dictionary<string, string> expected = new()
        {
            ["Temperature"] = "°C",
            ["Fan"] = "RPM",
            ["Voltage"] = "V",
            ["Clock"] = "MHz",
            ["Load"] = "%",
            ["Power"] = "W",
            ["Current"] = "A",
            ["Throughput"] = "MB/s",
            ["Frequency"] = "Hz",
            ["Control"] = "%",
            ["Level"] = "%",
            ["Data"] = "GB",
            ["SmallData"] = "MB",
            ["Flow"] = "L/h",
            ["Factor"] = "",
            ["TimeSpan"] = "s",
            ["Timing"] = "ns",
            ["Energy"] = "mWh",
            ["Noise"] = "dBA",
            ["Conductivity"] = "µS/cm",
            ["Humidity"] = "%",
            ["NotASensorType"] = "",
        };

        foreach ((string sensorType, string unit) in expected)
        {
            Assert.AreEqual(unit, LhmSharedMemoryReader.UnitFor(sensorType), $"UnitFor({sensorType})");
        }
    }

    // ── Poll policy through the ILhmMapSource seam ─────────────────

    [TestMethod]
    public void Poll_MapSourceReturnsValidMap_ParsesConnectedDto()
    {
        var source = new StubLhmMapSource { Bytes = BuildMap(JsonIndexFormat, CpuTemp, GpuFan) };
        var reader = new LhmSharedMemoryReader(source);

        SensorSnapshotDto dto = reader.Poll();

        Assert.IsTrue(dto.IsConnected);
        Assert.AreEqual(2, dto.Readings.Count);
        Assert.AreEqual("AMD Ryzen 7 5800X: CPU Package", dto.Readings[0].Label);
        Assert.IsNull(reader.LastError);
    }

    [TestMethod]
    public void Poll_MapSourceUnavailable_DisconnectedWithSourceError()
    {
        var source = new StubLhmMapSource { Error = "LHS sensor mutex not acquired within 100ms (writer holds it)" };
        var reader = new LhmSharedMemoryReader(source);

        SensorSnapshotDto dto = reader.Poll();

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual("LHS sensor mutex not acquired within 100ms (writer holds it)", reader.LastError);
    }

    [TestMethod]
    public void Poll_MapSourceReturnsNullWithoutError_DisconnectedWithGenericMessage()
    {
        var reader = new LhmSharedMemoryReader(new StubLhmMapSource());

        SensorSnapshotDto dto = reader.Poll();

        Assert.IsFalse(dto.IsConnected);
        StringAssert.Contains(reader.LastError, "unavailable");
    }

    [TestMethod]
    public void Poll_MalformedMap_DisconnectedAsUnreadable()
    {
        var source = new StubLhmMapSource { Bytes = [1, 2, 3] };
        var reader = new LhmSharedMemoryReader(source);

        SensorSnapshotDto dto = reader.Poll();

        Assert.IsFalse(dto.IsConnected);
        StringAssert.Contains(reader.LastError, "unreadable or malformed");
    }

    // ── the bounded copy policy (attacker-claimed sizes) ────────────

    private sealed class FakeMap : MemoryMappedLhmMapSource.IReadableMap
    {
        private readonly byte[] _bytes;
        public FakeMap(byte[] bytes) => _bytes = bytes;
        public long Capacity => _bytes.Length;
        public void ReadRange(long offset, byte[] buffer, int bufferOffset, int count)
            => Array.Copy(_bytes, offset, buffer, bufferOffset, count);
    }

    [TestMethod]
    public void CopyMapBytes_HeaderDeclaredSizesDriveCopyLength()
    {
        byte[] map = BuildMap(JsonIndexFormat, CpuTemp, GpuFan);
        var fake = new FakeMap(map);

        byte[] copied = MemoryMappedLhmMapSource.CopyMapBytes(fake);

        Assert.AreEqual(map.Length, copied.Length, "a well-formed map copies exactly its declared data extent");
        Assert.AreEqual(map.Length, fake.Capacity);
    }

    [TestMethod]
    public void CopyMapBytes_DeclaredDataBeyondMap_ClampsToCapacity()
    {
        byte[] map = BuildMap(JsonIndexFormat, CpuTemp);
        // Corrupt data-length to claim an extent far past the real map.
        int msb = MetadataBlockSize;
        WriteInt(map, msb + 12, int.MaxValue);
        var fake = new FakeMap(map);

        byte[] copied = MemoryMappedLhmMapSource.CopyMapBytes(fake);

        Assert.AreEqual(map.Length, copied.Length, "the copy clamps to the map's real capacity, never beyond");
    }

    [TestMethod]
    public void CopyMapBytes_ShortMap_ReturnsOnlyWhatExists()
    {
        byte[] map = BuildMap(JsonIndexFormat, CpuTemp);
        var fake = new FakeMap(map.AsSpan(0, 8).ToArray());

        byte[] copied = MemoryMappedLhmMapSource.CopyMapBytes(fake);

        Assert.AreEqual(8, copied.Length, "a map shorter than the fixed header copies only the available bytes");
    }

    // ── fixture helpers ─────────────────────────────────────────

    /// <summary>One well-formed sensors map (JSON index, two sensors) for
    /// cluster-level tests.</summary>
    internal static byte[] BuildSensorsMapFixture() => BuildMap(JsonIndexFormat, CpuTemp, GpuFan);

    private static byte[] BuildMap(int indexFormat, params SensorBlock[] sensors)
    {
        List<IndexEntry> entries = [];
        using var stream = new MemoryStream();
        foreach (SensorBlock sensor in sensors)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(sensor, CamelCase);
            entries.Add(new IndexEntry
            {
                Identifier = sensor.Identifier,
                Offset = (int)stream.Position, // relative to the start of the data blob
                Size = json.Length,
                SensorName = sensor.Name,
                SensorType = sensor.SensorType,
                HardwareName = sensor.HardwareName,
            });
            stream.Write(json);
            stream.WriteByte(0);
        }

        return BuildMap(indexFormat, entries, stream.ToArray());
    }

    private static byte[] BuildMap(int indexFormat, List<IndexEntry> entries, byte[] dataBytes)
    {
        byte[] indexBytes = indexFormat switch
        {
            JsonIndexFormat => JsonSerializer.SerializeToUtf8Bytes(entries, CamelCase),
            MessagePackIndexFormat => MessagePackSerializer.Serialize(entries),
            _ => throw new ArgumentOutOfRangeException(nameof(indexFormat)),
        };

        int indexOffset = MetadataBlockSize + 4 + (36 * 4); // writer: msb + reserved.Length + (lastFieldOffset * 4)
        int dataOffset = indexOffset + indexBytes.Length + 4; // writer: indexOffset + indexLen + 4 padding
        byte[] map = new byte[dataOffset + dataBytes.Length];

        WriteInt(map, 0, MetaDataSize);
        WriteInt(map, 4, 1000); // UpdateInterval
        WriteLong(map, 8, UnixNow);
        WriteInt(map, MetadataBlockSize, indexBytes.Length); // index-length
        WriteInt(map, MetadataBlockSize + 4, indexOffset); // index-offset
        WriteInt(map, MetadataBlockSize + 8, indexFormat); // index-format
        WriteInt(map, MetadataBlockSize + 12, dataBytes.Length); // data-length
        WriteInt(map, MetadataBlockSize + 16, dataOffset); // data-offset
        // reserved 16 bytes at MetadataBlockSize + 20 stay zero
        Array.Copy(indexBytes, 0, map, indexOffset, indexBytes.Length);
        Array.Copy(dataBytes, 0, map, dataOffset, dataBytes.Length);
        return map;
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteLong(byte[] buffer, int offset, long value)
    {
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 8), value);
    }
}
