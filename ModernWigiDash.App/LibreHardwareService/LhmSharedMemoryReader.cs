using System.Text.Json;
using MessagePack;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.LibreHardwareService;

/// <summary>App-side reader for LibreHardwareService's shared-memory sensor
/// maps. Owns the poll policy (map source → parse → outcome) and the pure
/// parsing (<see cref="TryParse"/>); the mutex/map/copy I/O lives behind
/// the injected <see cref="ILhmMapSource"/> seam — the reader never creates
/// a map source itself, so the production default is chosen once by the
/// owning module. Never throws — every failure yields a disconnected
/// snapshot, and <see cref="LastError"/> carries the reason for the poll
/// tick to log once per change.
/// </summary>
public sealed class LhmSharedMemoryReader
{
    // LibreHardwareService (epinter) header layout — mirrored from its
    // MemoryMappedSensors source. The metadata block is 4 + MetadataSize
    // (MetadataSize is sizeof(int)+sizeof(long) = 12, so the index/data
    // fields start at 16 on a stock install; the reader honors a variable
    // metadata block size regardless).
    internal const int OffsetMetaDataSize = 0;
    internal const int OffsetLastUpdate = 8;
    internal const int OffsetIndexLength = 16;
    internal const int OffsetReserved = 36;

    // Fixed 52-byte header block: fields run OffsetIndexLength..OffsetReserved+16
    // (IndexLength..DataOffset is 20 bytes + 16 reserved).
    internal const int FieldsBlockSize = OffsetReserved + 16 - OffsetIndexLength;
    internal const int FixedHeaderSize = OffsetReserved + 16;
    internal const int IndexFormatJson = 1;
    internal const int IndexFormatMessagePack = 2;

    // Attacker-claimed map/index caps: any local process can pre-create the map
    // before the LHS service starts, so header-declared sizes are untrusted.
    internal const int MaxSensorEntries = 5000;
    internal const int MaxSensorBlockBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SensorJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILhmMapSource _mapSource;

    /// <summary>The clock for the disconnected-snapshot timestamp (test seam —
    /// production binds the system clock; tests inject a fake to pin the
    /// stamp).</summary>
    internal TimeProvider Clock { get; }

    /// <param name="mapSource">The map I/O adapter (memory-mapped production
    /// adapter or an in-memory fake); required — the default is chosen by the
    /// owning module, not the reader.</param>
    /// <param name="timeProvider">Test seam: clock for the disconnected
    /// snapshot's timestamp (defaults to the system clock).</param>
    public LhmSharedMemoryReader(ILhmMapSource mapSource, TimeProvider? timeProvider = null)
    {
        _mapSource = mapSource;
        Clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reason the last <see cref="Poll"/> failed, or null when the last poll
    /// succeeded. The poll tick compares this across ticks to log once per change.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// One full read of the LHS sensors map — the map source performs the
    /// mutex-guarded bounded copy, the parse runs outside any lock. Never throws.
    /// </summary>
    public SensorSnapshotDto Poll()
    {
        try
        {
            byte[]? mapBytes = _mapSource.TryReadSensorsMap(out string? error);
            if (mapBytes is null)
            {
                return Disconnected(error ?? "LHS sensors map unavailable");
            }

            SensorSnapshotDto dto = TryParse(mapBytes);
            if (!dto.IsConnected)
            {
                return Disconnected("LHS sensors map unreadable or malformed");
            }

            LastError = null;
            return dto;
        }
        catch (Exception ex)
        {
            return Disconnected($"LHS sensors map unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure parser: header → index (JSON or MessagePack per index-format) → data
    /// blocks → DTO. Any malformed input yields a disconnected snapshot.
    /// </summary>
    internal static SensorSnapshotDto TryParse(byte[] mapBytes)
    {
        try
        {
            if (!TryReadHeader(mapBytes, out long lastUpdate, out int msb))
                return DisconnectedSnapshot();
            if (!TryReadFields(mapBytes, msb, out int indexLength, out int indexOffset, out int indexFormat, out int dataLength, out int dataOffset))
                return DisconnectedSnapshot();

            List<IndexEntry>? entries = ParseIndex(mapBytes, indexFormat, indexOffset, indexLength);
            if (entries is null || entries.Count > MaxSensorEntries)
                return DisconnectedSnapshot();

            List<SensorReadingDto>? readings = MapReadings(mapBytes, dataOffset, dataLength, entries);
            if (readings is null)
                return DisconnectedSnapshot();

            return new SensorSnapshotDto
            {
                IsConnected = true,
                LastUpdate = DateTimeOffset.FromUnixTimeSeconds(lastUpdate).UtcDateTime,
                Readings = readings,
            };
        }
        catch (Exception)
        {
            return DisconnectedSnapshot();
        }
    }

    /// <summary>The fixed header block: metadata size and last-update stamp,
    /// both validated before the variable metadata block is addressed.</summary>
    private static bool TryReadHeader(byte[] mapBytes, out long lastUpdate, out int msb)
    {
        lastUpdate = 0;
        msb = 0;
        if (mapBytes.Length < FixedHeaderSize) return false;

        int metaDataSize = BitConverter.ToInt32(mapBytes, OffsetMetaDataSize);
        lastUpdate = BitConverter.ToInt64(mapBytes, OffsetLastUpdate);
        if (lastUpdate <= 0 || metaDataSize < 0) return false;

        msb = 4 + metaDataSize;
        return msb + FieldsBlockSize <= mapBytes.Length;
    }

    /// <summary>The 20-byte index/data descriptor block, bounds-checked against
    /// the map length — declared sizes from an untrusted map never address past
    /// the copy.</summary>
    private static bool TryReadFields(byte[] mapBytes, int msb, out int indexLength, out int indexOffset, out int indexFormat, out int dataLength, out int dataOffset)
    {
        indexLength = BitConverter.ToInt32(mapBytes, msb + 0);
        indexOffset = BitConverter.ToInt32(mapBytes, msb + 4);
        indexFormat = BitConverter.ToInt32(mapBytes, msb + 8);
        dataLength = BitConverter.ToInt32(mapBytes, msb + 12);
        dataOffset = BitConverter.ToInt32(mapBytes, msb + 16);

        if (indexLength < 0 || indexOffset < 0 || dataLength < 0 || dataOffset < 0) return false;
        if ((long)indexOffset + indexLength > mapBytes.Length) return false;
        if ((long)dataOffset + dataLength > mapBytes.Length) return false;
        return true;
    }

    /// <summary>Decodes the index per the declared format; null for an unknown
    /// format.</summary>
    private static List<IndexEntry>? ParseIndex(byte[] mapBytes, int indexFormat, int indexOffset, int indexLength)
        => indexFormat switch
        {
            IndexFormatJson => JsonSerializer.Deserialize<List<IndexEntry>>(
                mapBytes.AsSpan(indexOffset, indexLength), SensorJsonOptions) ?? [],
            IndexFormatMessagePack => MessagePackSerializer.Deserialize<List<IndexEntry>>(
                mapBytes.AsMemory(indexOffset, indexLength)),
            _ => null,
        };

    /// <summary>Reads each sensor block; null when any entry escapes the data
    /// bounds or exceeds the per-block cap.</summary>
    private static List<SensorReadingDto>? MapReadings(byte[] mapBytes, int dataOffset, int dataLength, List<IndexEntry> entries)
    {
        List<SensorReadingDto> readings = new(entries.Count);
        foreach (IndexEntry entry in entries)
        {
            if (entry.Offset < 0 || entry.Size < 0 || (long)entry.Offset + entry.Size > dataLength)
                return null;
            if (entry.Size > MaxSensorBlockBytes)
                return null;

            byte[] json = mapBytes.AsSpan(dataOffset + entry.Offset, entry.Size).ToArray();
            SensorBlock block = JsonSerializer.Deserialize<SensorBlock>(json, SensorJsonOptions)
                ?? throw new JsonException("empty sensor block");

            readings.Add(new SensorReadingDto
            {
                SensorId = block.Identifier,
                SensorName = block.Name,
                HardwareName = block.HardwareName,
                HardwareType = block.HardwareType,
                SensorType = block.SensorType,
                Unit = UnitFor(block.SensorType),
                Value = block.Value,
                Min = block.Min,
                Max = block.Max,
            });
        }
        return readings;
    }

    /// <summary>
    /// Replicates LibreHardwareService's <c>UnitFor</c> mapping, string-keyed
    /// on the LHS <c>SensorType.ToString()</c> values.
    /// </summary>
    internal static string UnitFor(string sensorType) => sensorType switch
    {
        "Voltage" => "V",
        "Current" => "A",
        "Power" => "W",
        "Clock" => "MHz",
        "Temperature" => "°C",
        "Load" => "%",
        "Frequency" => "Hz",
        "Fan" => "RPM",
        "Flow" => "L/h",
        "Control" => "%",
        "Level" => "%",
        "Factor" => "",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "MB/s",
        "TimeSpan" => "s",
        "Timing" => "ns",
        "Energy" => "mWh",
        "Noise" => "dBA",
        "Conductivity" => "µS/cm",
        "Humidity" => "%",
        _ => "",
    };

    private SensorSnapshotDto Disconnected(string error)
    {
        LastError = error;
        return new SensorSnapshotDto
        {
            IsConnected = false,
            LastUpdate = Clock.GetUtcNow().UtcDateTime,
            Readings = [],
        };
    }

    private static SensorSnapshotDto DisconnectedSnapshot() => new()
    {
        IsConnected = false,
        Readings = [],
    };

    /// <summary>
    /// One index entry, mirroring LHS <c>DataIndex</c>: MessagePack keys in this
    /// exact order, JSON field names camelCase (parsed case-insensitively).
    /// Offsets are relative to the start of the data blob.
    /// </summary>
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class IndexEntry
    {
        [Key(0)]
        public string Identifier { get; set; } = string.Empty;

        [Key(1)]
        public int Offset { get; set; }

        [Key(2)]
        public int Size { get; set; }

        [Key(3)]
        public string SensorName { get; set; } = string.Empty;

        [Key(4)]
        public string SensorType { get; set; } = string.Empty;

        [Key(5)]
        public string HardwareName { get; set; } = string.Empty;
    }

    /// <summary>
    /// One LHS <c>DataSensor</c> JSON block (camelCase fields); the
    /// <c>valuesTimeWindow</c>/<c>values</c> members are deliberately ignored.
    /// <c>hardwareId</c> is ignored by the reader (the DTO carries HardwareName,
    /// not the internal id) and is not deserialized.
    /// </summary>
    internal sealed record SensorBlock(
        string Identifier,
        string Name,
        string SensorType,
        string HardwareName,
        string HardwareType,
        double Value,
        double Min,
        double Max);
}
