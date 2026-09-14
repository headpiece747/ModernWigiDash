using System.IO;
using System.IO.MemoryMappedFiles;
using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Tests;

/// <summary>
/// The production map adapter's policy through its injected seams, so the real
/// named-map specifics are not needed: the mutex is best-effort (the vendor
/// denies a non-elevated process) and a copy still runs without it. Pins the
/// 2026-09-13 bug where an unopenable mutex failed every read.
/// </summary>
[TestClass]
public sealed class MemoryMappedAidaMmapSourceTests
{
    [TestMethod]
    public void TryRead_MutexUnopenable_StillReadsTheMap()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        WriteBytes(map, 0, [1, 2, 3, 4]);
        using var source = new MemoryMappedAidaMmapSource(
            _ => throw new UnauthorizedAccessException("denied"),
            _ => map);

        var destination = new byte[4];
        Assert.IsTrue(source.TryRead(0, 4, destination, out string? error));
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, destination);
    }

    [TestMethod]
    public void TryRead_MutexAcquired_ReadsAndReleases()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        WriteBytes(map, 8, [9, 8, 7]);
        using var mutex = new Mutex(false);
        using var source = new MemoryMappedAidaMmapSource(_ => mutex, _ => map);

        var destination = new byte[3];
        Assert.IsTrue(source.TryRead(8, 3, destination, out _));
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, destination);
    }

    [TestMethod]
    public void TryRead_RangeBeyondTheMap_FailsWithTheMapTooSmallError()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        using var source = new MemoryMappedAidaMmapSource(_ => throw new UnauthorizedAccessException(), _ => map);

        Assert.IsFalse(source.TryRead(4000, 200, new byte[200], out string? error));
        Assert.AreEqual("AIDA64 map too small for the requested range", error);
    }

    [TestMethod]
    public void TryRead_DestinationTooShort_FailsBeforeTouchingTheMap()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        using var source = new MemoryMappedAidaMmapSource(_ => throw new UnauthorizedAccessException(), _ => map);

        Assert.IsFalse(source.TryRead(0, 20, new byte[10], out string? error));
        Assert.AreEqual("AIDA64 map read request out of bounds", error);
    }

    [TestMethod]
    public void TryRead_MapMissing_ReportsUnavailable()
    {
        using var source = new MemoryMappedAidaMmapSource(
            _ => throw new UnauthorizedAccessException(),
            _ => throw new FileNotFoundException("no map"));

        Assert.IsFalse(source.TryRead(0, 4, new byte[4], out string? error));
        StringAssert.Contains(error!, "AIDA64 map unavailable");
    }

    [TestMethod]
    public void Dispose_IsIdempotentAndReadsAfterDisposeAreRefused()
    {
        var source = new MemoryMappedAidaMmapSource(_ => throw new FileNotFoundException(), _ => throw new FileNotFoundException());
        source.Dispose();
        source.Dispose();

        Assert.IsFalse(source.TryRead(0, 4, new byte[4], out _));
    }

    private static void WriteBytes(MemoryMappedFile map, int offset, byte[] bytes)
    {
        using var accessor = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Write);
        accessor.WriteArray(offset, bytes, 0, bytes.Length);
    }
}
