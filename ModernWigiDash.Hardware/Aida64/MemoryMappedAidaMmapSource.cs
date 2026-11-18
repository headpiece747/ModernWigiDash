using System.IO.MemoryMappedFiles;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The production <see cref="IAidaMmapSource"/> adapter over the vendor
/// AIDA64 panel's named shared map. The map handle is opened lazily on the
/// first read and re-opened after a failed read; ACL denial on the mutex
/// latches unlocked, while a transient mutex absence retries every 5 seconds.
/// </summary>
public sealed class MemoryMappedAidaMmapSource : IAidaMmapSource
{
    internal const string MapName = @"Global\Gskill_Frontier_Aida64Widget";
    internal const string MutexName = @"Global\Gskill_Frontier_Aida64WidgetMutex";

    private readonly Func<string, Mutex?> _openMutex;
    private readonly Func<string, MemoryMappedFile> _openMap;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly System.Threading.Lock _mutexGate = new();
    private readonly LogOnChange _mutexLog = new();
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _accessor;
    private Mutex? _mutex;
    private bool _mutexDenied;
    private DateTimeOffset _nextMutexProbeUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>
    /// Binds the production seams: the named mutex and the named read-only map.
    /// </summary>
    public MemoryMappedAidaMmapSource()
        : this(Mutex.OpenExisting, name => MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
    {
    }

    internal MemoryMappedAidaMmapSource(
        Func<string, Mutex?> openMutex,
        Func<string, MemoryMappedFile> openMap,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _openMutex = openMutex;
        _openMap = openMap;
        _clock = timeProvider ?? TimeProvider.System;
        _log = log ?? FileLog.Write;
    }

    /// <inheritdoc />
    public bool TryRead(int offset, int length, byte[] destination, out string? error)
    {
        // `offset` indexes the MAP and `length` indexes the DESTINATION: the
        // guard must compare each against its own bound. (Checking
        // offset + length against the destination length rejected every read
        // at a nonzero offset, i.e. the bitmap header and the pixels, so the
        // reader could never produce a frame.)
        if (offset < 0 || length < 0 || destination is null || length > destination.Length)
        {
            error = "AIDA64 map read request out of bounds";
            return false;
        }

        lock (_mutexGate)
        {
            if (_disposed)
            {
                error = "AIDA64 map source disposed";
                return false;
            }

            // The mutex is probed ONCE for the source's lifetime unless missing
            // transiently (5 s cooldown): the vendor's descriptor denies a
            // non-elevated process, so OpenExisting threw on every read before
            // this. A null handle means "copy unlocked", never "map down"; the
            // map-open failure below is the liveness signal.
            Mutex? mutex = GetMutexLocked();
            bool acquired = false;
            if (mutex is not null)
            {
                bool usable = true;
                try
                {
                    acquired = NamedMapAcquirePolicy.TryAcquire(mutex);
                }
                catch (Exception)
                {
                    // A wedged handle is not a map failure. Fall through to an
                    // unlocked copy. (This is the one rule the LHM adapter reads the
                    // other way round, on purpose - the shared policy lets every
                    // exception except the abandoned one through precisely so each
                    // adapter keeps its own answer to "what does an unusable handle
                    // mean".)
                    usable = false;
                }

                if (usable && !acquired)
                {
                    error = NamedMapAcquirePolicy.NotAcquiredMessage("AIDA64 map mutex");
                    return false;
                }
            }

            try
            {
                if (!EnsureAccessor(out error))
                {
                    return false;
                }

                // 64-bit compare: a producer-controlled offset near int.MaxValue made
                // the 32-bit sum wrap negative and pass this guard, leaving the
                // framework to reject the read (a per-frame close/reopen churn).
                if ((long)offset + length > _accessor!.Capacity)
                {
                    error = "AIDA64 map too small for the requested range";
                    return false;
                }

                _accessor.ReadArray(offset, destination, 0, length);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                DisposeAccessor();
                error = $"AIDA64 map read failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (acquired && mutex is not null)
                {
                    NamedMapAcquirePolicy.Release(mutex);
                }
            }
        }
    }

    /// <summary>
    /// The cached mutex handle: ACL-denial latches unlocked, whereas a transient
    /// absence retries on a 5-second cooldown so late-starting producers are picked up.
    /// Caller holds <see cref="_mutexGate"/>.
    /// </summary>
    private Mutex? GetMutexLocked()
    {
        if (_disposed)
            return null;
        if (_mutex is not null)
            return _mutex;
        if (_mutexDenied)
            return null;

        var now = _clock.GetUtcNow();
        if (now < _nextMutexProbeUtc)
            return null;

        try
        {
            var opened = _openMutex(MutexName);
            if (opened is not null)
            {
                _mutex = opened;
                _mutexLog.Changed("acquired");
                return _mutex;
            }

            _nextMutexProbeUtc = now + TimeSpan.FromSeconds(5);
            if (_mutexLog.Changed("unavailable"))
            {
                _log("[AIDA-MAP] vendor mutex unavailable; retrying periodically");
            }
        }
        catch (UnauthorizedAccessException)
        {
            _mutexDenied = true;
            _mutex = null;
            if (_mutexLog.Changed("denied"))
            {
                _log("[AIDA-MAP] vendor mutex denied; reading unlocked");
            }
        }
        catch (Exception)
        {
            _nextMutexProbeUtc = now + TimeSpan.FromSeconds(5);
            _mutex = null;
            if (_mutexLog.Changed("unavailable"))
            {
                _log("[AIDA-MAP] vendor mutex unavailable; retrying periodically");
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_mutexGate)
        {
            if (_disposed) return;
            DisposeAccessor();
            _nextMutexProbeUtc = DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_mutexGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeAccessor();
            _mutex?.Dispose();
            _mutex = null;
        }
    }

    private bool EnsureAccessor(out string? error)
    {
        if (_accessor is not null)
        {
            error = null;
            return true;
        }

        try
        {
            _map ??= _openMap(MapName);
            _accessor = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            DisposeAccessor();
            error = $"AIDA64 map unavailable: {ex.Message}";
            return false;
        }
    }

    private void DisposeAccessor()
    {
        _accessor?.Dispose();
        _accessor = null;
        _map?.Dispose();
        _map = null;
    }
}
