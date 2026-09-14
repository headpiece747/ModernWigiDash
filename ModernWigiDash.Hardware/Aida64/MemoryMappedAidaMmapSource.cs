using System.IO.MemoryMappedFiles;

namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The production <see cref="IAidaMmapSource"/> adapter over the vendor
/// AIDA64 panel's named shared map. The map handle is opened lazily on the
/// first read and re-opened after a failed read; the mutex is probed fresh per
/// read, best-effort (it is not openable without elevation, and an absent
/// mutex falls back to a mutex-less copy, the map-open failure being the
/// liveness signal for AIDA64 being down).
/// </summary>
public sealed class MemoryMappedAidaMmapSource : IAidaMmapSource
{
    internal const string MapName = @"Global\Gskill_Frontier_Aida64Widget";
    internal const string MutexName = @"Global\Gskill_Frontier_Aida64WidgetMutex";
    internal const long MapSize = 8 * 1024 * 1024;
    internal static readonly TimeSpan MutexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Func<string, Mutex?> _openMutex;
    private readonly Func<string, MemoryMappedFile> _openMap;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _accessor;
    private bool _disposed;

    /// <summary>
    /// Binds the production seams: the named mutex and the named read-only map.
    /// </summary>
    public MemoryMappedAidaMmapSource()
        : this(Mutex.OpenExisting, name => MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
    {
    }

    internal MemoryMappedAidaMmapSource(Func<string, Mutex?> openMutex, Func<string, MemoryMappedFile> openMap)
    {
        _openMutex = openMutex;
        _openMap = openMap;
    }

    /// <inheritdoc />
    public bool TryRead(int offset, int length, byte[] destination, out string? error)
    {
        if (_disposed)
        {
            error = "AIDA64 map source disposed";
            return false;
        }

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

        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            mutex = _openMutex(MutexName);
        }
        catch
        {
            // Best-effort: the vendor creates the mutex with a descriptor that
            // denies a non-elevated process, and an absent mutex means AIDA64
            // is fully down, which the map-open failure below reports. Either
            // way the copy is attempted; the reader validates the header.
        }

        if (mutex is not null)
        {
            bool usable = true;
            try
            {
                acquired = mutex.WaitOne(MutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            catch
            {
                // A wedged handle is not a map failure. Fall through to an
                // unlocked copy.
                usable = false;
            }

            if (usable && !acquired)
            {
                mutex.Dispose();
                error = "AIDA64 map mutex not acquired within 100 ms (writer holds it)";
                return false;
            }
        }

        try
        {
            if (!EnsureAccessor(out error))
            {
                return false;
            }

            if (offset + length > _accessor!.Capacity)
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
            if (mutex is not null)
            {
                if (acquired)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // The writer's process may have exited while the mutex
                        // was held. Releasing an orphaned mutex is best-effort.
                    }
                }

                mutex.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeAccessor();
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
