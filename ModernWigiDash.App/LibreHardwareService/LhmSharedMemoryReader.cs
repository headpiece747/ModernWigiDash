using System.IO.MemoryMappedFiles;
using System.Text.Json;
using MessagePack;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.LibreHardwareService;

/// <summary>
/// App-side reader for LibreHardwareService's named shared-memory sensor maps.
/// Owns the mutex-guarded read (map name, mutex, bounded copy) and delegates the
/// pure parsing to <see cref="TryParse"/>. Never throws — every failure yields a
/// disconnected snapshot, and <see cref="LastError"/> carries the reason for the
/// poll tick to log once per change.
/// </summary>
public sealed class LhmSharedMemoryReader
{
    // LibreHardwareService (epinter) constants — mirrored from its
    // MemoryMappedSensors and Constants sources. The metadata block is
    // 4 + MetadataSize (MetadataSize is sizeof(int)+sizeof(long) = 12, so the
    // index/data fields start at 16 on a stock install; the reader honors a
    // variable metadata block size regardless).
    internal const string SensorsMapName = @"Global\LibreHardwareService/json/sensors/data";
    internal const string SensorsMutexName = @"Global\LibreHardwareService/json/sensors/data/MUTEX";

    internal const int OffsetMetaDataSize = 0;
    internal const int OffsetUpdateInterval = 4;
    internal const int OffsetLastUpdate = 8;
    internal const int OffsetIndexLength = 16;
    internal const int OffsetIndexOffset = 20;
    internal const int OffsetIndexFormat = 24;
    internal const int OffsetDataLength = 28;
    internal const int OffsetDataOffset = 32;
    internal const int OffsetReserved = 36;

    // Fixed 52-byte header block: fields run OffsetIndexLength..OffsetReserved+16
    // (OffsetIndexLength..OffsetDataOffset is 20 bytes + 16 reserved).
    internal const int FieldsBlockSize = OffsetReserved + 16 - OffsetIndexLength;
    internal const int FixedHeaderSize = OffsetReserved + 16;
    internal const int IndexFormatJson = 1;
    internal const int IndexFormatMessagePack = 2;

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions SensorJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reason the last <see cref="Poll"/> failed, or null when the last poll
    /// succeeded. The poll tick compares this across ticks to log once per change.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// One full mutex-guarded read of the LHS sensors map, parsed into a
    /// <see cref="SensorSnapshotDto"/>. Never throws.
    /// </summary>
    public SensorSnapshotDto Poll()
    {
        try
        {
            using Mutex mutex = Mutex.OpenExisting(SensorsMutexName);
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(MutexTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // only the service writes; a dead writer's mutex is safe to continue under
                }

                if (!acquired)
                    return Disconnected("LHS sensor mutex not acquired within 100ms (writer holds it)");

                using MemoryMappedFile map = MemoryMappedFile.OpenExisting(SensorsMapName, MemoryMappedFileRights.Read);
                using MemoryMappedViewAccessor accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                SensorSnapshotDto dto = TryParse(CopyMapBytes(accessor));
                if (!dto.IsConnected)
                    return Disconnected("LHS sensors map unreadable or malformed");

                LastError = null;
                return dto;
            }
            finally
            {
                if (acquired)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // abandoned mutex — nothing to release
                    }
                }
            }
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
            if (mapBytes.Length < FixedHeaderSize)
                return DisconnectedSnapshot();

            int metaDataSize = BitConverter.ToInt32(mapBytes, OffsetMetaDataSize);
            long lastUpdate = BitConverter.ToInt64(mapBytes, OffsetLastUpdate);

            if (lastUpdate <= 0 || metaDataSize < 0)
                return DisconnectedSnapshot();

            long msb = 4L + metaDataSize;
            if (msb + FieldsBlockSize > mapBytes.Length)
                return DisconnectedSnapshot();

            int indexLength = BitConverter.ToInt32(mapBytes, (int)msb + 0);
            int indexOffset = BitConverter.ToInt32(mapBytes, (int)msb + 4);
            int indexFormat = BitConverter.ToInt32(mapBytes, (int)msb + 8);
            int dataLength = BitConverter.ToInt32(mapBytes, (int)msb + 12);
            int dataOffset = BitConverter.ToInt32(mapBytes, (int)msb + 16);

            if (indexLength < 0 || indexOffset < 0 || dataLength < 0 || dataOffset < 0)
                return DisconnectedSnapshot();
            if ((long)indexOffset + indexLength > mapBytes.Length)
                return DisconnectedSnapshot();
            if ((long)dataOffset + dataLength > mapBytes.Length)
                return DisconnectedSnapshot();

            List<IndexEntry>? entries = indexFormat switch
            {
                IndexFormatJson => JsonSerializer.Deserialize<List<IndexEntry>>(
                    mapBytes.AsSpan(indexOffset, indexLength), SensorJsonOptions) ?? [],
                IndexFormatMessagePack => MessagePackSerializer.Deserialize<List<IndexEntry>>(
                    mapBytes.AsMemory(indexOffset, indexLength)),
                _ => null,
            };
            if (entries is null)
                return DisconnectedSnapshot();

            List<SensorReadingDto> readings = new(entries.Count);
            foreach (IndexEntry entry in entries)
            {
                if (entry.Offset < 0 || entry.Size < 0 || (long)entry.Offset + entry.Size > dataLength)
                    return DisconnectedSnapshot();

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
                    Avg = 0, // LHS publishes value/min/max only; the widget falls back to Max
                });
            }

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

    /// <summary>
    /// Replicates the Service-side <c>LhmSensorReader.UnitFor</c> table, string-keyed
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
            LastUpdate = DateTime.UtcNow,
            Readings = [],
        };
    }

    private static SensorSnapshotDto DisconnectedSnapshot() => new()
    {
        IsConnected = false,
        Readings = [],
    };

    /// <summary>
    /// Bounded copy of the map: reads the header first to size the copy
    /// (dataOffset + dataLength), so a 64MB map is never copied whole.
    /// Bounds/format validation is left to <see cref="TryParse"/>.
    /// </summary>
    private static byte[] CopyMapBytes(MemoryMappedViewAccessor accessor)
    {
        long capacity = accessor.Capacity;
        int prefix = (int)Math.Min(capacity, FixedHeaderSize);
        byte[] buffer = new byte[prefix];
        if (prefix > 0)
            accessor.ReadArray(0, buffer, 0, prefix);

        if (prefix < FixedHeaderSize)
            return buffer;

        int metaDataSize = BitConverter.ToInt32(buffer, OffsetMetaDataSize);
        long msb = 4L + metaDataSize;
        if (msb < 0 || msb + FieldsBlockSize > capacity)
            return buffer;

        if (msb + FieldsBlockSize > buffer.Length)
            buffer = CopyRange(accessor, buffer, (int)(msb + FieldsBlockSize));

        int dataLength = BitConverter.ToInt32(buffer, (int)msb + 12);
        int dataOffset = BitConverter.ToInt32(buffer, (int)msb + 16);

        long total = (long)dataOffset + dataLength;
        if (total <= buffer.Length)
            return buffer;
        total = Math.Min(total, capacity);
        if (total > buffer.Length)
            buffer = CopyRange(accessor, buffer, (int)total);

        return buffer;
    }

    private static byte[] CopyRange(MemoryMappedViewAccessor accessor, byte[] prefix, int total)
    {
        byte[] result = new byte[total];
        Array.Copy(prefix, result, prefix.Length);
        accessor.ReadArray(prefix.Length, result, prefix.Length, total - prefix.Length);
        return result;
    }

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
    /// </summary>
    internal sealed record SensorBlock(
        string Identifier,
        string Name,
        string SensorType,
        string HardwareId,
        string HardwareName,
        string HardwareType,
        double Value,
        double Min,
        double Max);
}
