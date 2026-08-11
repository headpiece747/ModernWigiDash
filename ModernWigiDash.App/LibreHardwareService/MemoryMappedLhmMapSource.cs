using System.IO.MemoryMappedFiles;

namespace ModernWigiDash.App.LibreHardwareService;

/// <summary>
/// The real <see cref="ILhmMapSource"/> adapter: opens LibreHardwareService's
/// named sensors map under its writer mutex and performs the bounded copy.
/// Every failure maps to null + a reason string — never throws.
/// </summary>
public sealed class MemoryMappedLhmMapSource : ILhmMapSource
{
    // LibreHardwareService (epinter) constants — mirrored from its
    // MemoryMappedSensors source; the mutex name protects the map.
    internal const string SensorsMapName = @"Global\LibreHardwareService/json/sensors/data";
    internal const string SensorsMutexName = @"Global\LibreHardwareService/json/sensors/data/MUTEX";

    // Real LHS maps are hundreds of KB; 8MB caps a misbehaving writer's
    // claimed size so a transient copy never forces a 128MB LOH allocation.
    private const int MaxCopyBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Func<string, Mutex> _openMutex;
    private readonly Func<string, MemoryMappedFile> _openMap;

    /// <summary>Production construction: the real named mutex/map openers.</summary>
    public MemoryMappedLhmMapSource()
        : this(Mutex.OpenExisting, name => MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
    {
    }

    /// <summary>
    /// Test seam: injected named-mutex/map openers (the WinUsbApi delegate-bag
    /// shape), so the missing-map, locked-mutex, and copy outcomes are
    /// scriptable without LibreHardwareService running.
    /// </summary>
    internal MemoryMappedLhmMapSource(Func<string, Mutex> openMutex, Func<string, MemoryMappedFile> openMap)
    {
        _openMutex = openMutex;
        _openMap = openMap;
    }

    public byte[]? TryReadSensorsMap(out string? error)
    {
        error = null;
        try
        {
            using Mutex mutex = _openMutex(SensorsMutexName);
            bool acquired = false;
            byte[] mapBytes;
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
                {
                    error = "LHS sensor mutex not acquired within 100ms (writer holds it)";
                    return null;
                }

                using MemoryMappedFile map = _openMap(SensorsMapName);
                using MemoryMappedViewAccessor accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                mapBytes = CopyMapBytes(new AccessorMap(accessor));
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

            return mapBytes;
        }
        catch (Exception ex)
        {
            error = $"LHS sensors map unavailable: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The bounded copy source abstraction: the accessor's capacity and range
    /// reads, isolated so the copy policy is unit-testable over an in-memory
    /// fake (two adapters — a real seam).
    /// </summary>
    internal interface IReadableMap
    {
        long Capacity { get; }
        void ReadRange(long offset, byte[] buffer, int bufferOffset, int count);
    }

    private sealed class AccessorMap : IReadableMap
    {
        private readonly MemoryMappedViewAccessor _accessor;
        public AccessorMap(MemoryMappedViewAccessor accessor) => _accessor = accessor;
        public long Capacity => _accessor.Capacity;
        public void ReadRange(long offset, byte[] buffer, int bufferOffset, int count)
            => _accessor.ReadArray(offset, buffer, bufferOffset, count);
    }

    /// <summary>
    /// Bounded copy of the map: reads the header first to size the copy
    /// (dataOffset + dataLength), so a 64MB map is never copied whole, and
    /// clamps every copy stage to <see cref="MaxCopyBytes"/> because the
    /// header-declared sizes are attacker-controlled. A clamped short copy is
    /// rejected by the reader's parse bounds checks.
    /// </summary>
    internal static byte[] CopyMapBytes(IReadableMap map)
    {
        long capacity = map.Capacity;
        // The header read is bounded by the capacity (a read past capacity
        // throws), so the first FixedHeaderSize bytes are always safe.
        int prefix = (int)Math.Min(capacity, LhmSharedMemoryReader.FixedHeaderSize);
        byte[] buffer = new byte[prefix];
        if (prefix > 0)
        {
            map.ReadRange(0, buffer, 0, prefix);
        }

        if (prefix < LhmSharedMemoryReader.FixedHeaderSize)
        {
            return buffer;
        }

        int metaDataSize = BitConverter.ToInt32(buffer, LhmSharedMemoryReader.OffsetMetaDataSize);
        long msb = 4L + metaDataSize;
        int fieldsEnd = (int)(msb + LhmSharedMemoryReader.FieldsBlockSize);
        if (msb < 0 || fieldsEnd > capacity)
        {
            return buffer;
        }
        if (fieldsEnd > MaxCopyBytes)
        {
            return buffer; // claimed metadata block unreachable within the copy cap — malformed
        }

        if (fieldsEnd > buffer.Length)
        {
            buffer = CopyRange(map, buffer, fieldsEnd);
        }

        int dataLength = BitConverter.ToInt32(buffer, (int)msb + 12);
        int dataOffset = BitConverter.ToInt32(buffer, (int)msb + 16);

        long total = (long)dataOffset + dataLength;
        if (total <= buffer.Length)
        {
            return buffer;
        }
        total = Math.Min(total, MaxCopyBytes);
        total = Math.Min(total, capacity);
        if (total > buffer.Length)
        {
            buffer = CopyRange(map, buffer, (int)total);
        }

        return buffer;
    }

    private static byte[] CopyRange(IReadableMap map, byte[] prefix, int total)
    {
        byte[] result = new byte[total];
        Array.Copy(prefix, result, prefix.Length);
        map.ReadRange(prefix.Length, result, prefix.Length, total - prefix.Length);
        return result;
    }
}
