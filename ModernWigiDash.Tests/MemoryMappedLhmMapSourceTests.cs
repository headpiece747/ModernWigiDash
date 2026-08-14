using System.IO;
using System.IO.MemoryMappedFiles;
using ModernWigiDash.App.LibreHardwareService;

namespace ModernWigiDash.Tests;

/// <summary>
/// The real map adapter's I/O policy through the delegate seam (the WinUsbApi
/// shape): missing mutex/map, a locked mutex, and the successful bounded copy —
/// scripted with real (anonymous) kernel objects, no LibreHardwareService.
/// </summary>
[TestClass]
public class MemoryMappedLhmMapSourceTests
{
    [TestMethod]
    public void TryReadSensorsMap_MutexMissing_ReturnsNullWithError()
    {
        var source = new MemoryMappedLhmMapSource(
            openMutex: _ => throw new WaitHandleCannotBeOpenedException(),
            openMap: _ => throw new FileNotFoundException());

        byte[]? bytes = source.TryReadSensorsMap(out string? error);

        Assert.IsNull(bytes);
        StringAssert.Contains(error, "LHS sensors map unavailable");
    }

    [TestMethod]
    public void TryReadSensorsMap_MapMissing_ReturnsNullWithError()
    {
        var source = new MemoryMappedLhmMapSource(
            openMutex: _ => new Mutex(),
            openMap: _ => throw new FileNotFoundException());

        byte[]? bytes = source.TryReadSensorsMap(out string? error);

        Assert.IsNull(bytes);
        StringAssert.Contains(error, "LHS sensors map unavailable");
    }

    [TestMethod]
    public void TryReadSensorsMap_LockedMutex_TimesOutWithAcquisitionError()
    {
        // The source's WaitOne runs on the test thread, so the lock must be
        // held by a DIFFERENT thread — a thread that already owns the mutex
        // would recursively acquire it and the timeout never fires. The source
        // opens its own handle (OpenExisting), so the owner's handle stays
        // valid for the release.
        string mutexName = "WMD-LockedMutex-" + Guid.NewGuid().ToString("N");
        using var ownerHolds = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var held = new Mutex(initiallyOwned: true, mutexName);
            ownerHolds.Set();
            releaseOwner.Wait();
        })
        {
            IsBackground = true,
            Name = "LhmLockedMutex-owner"
        };
        owner.Start();
        ownerHolds.Wait();

        try
        {
            var source = new MemoryMappedLhmMapSource(
                openMutex: _ => Mutex.OpenExisting(mutexName),
                openMap: _ => throw new FileNotFoundException());

            byte[]? bytes = source.TryReadSensorsMap(out string? error);

            Assert.IsNull(bytes);
            Assert.AreEqual("LHS sensor mutex not acquired within 100ms (writer holds it)", error);
        }
        finally
        {
            releaseOwner.Set();
            owner.Join();
        }
    }

    [TestMethod]
    public void TryReadSensorsMap_CopySucceeds_ReturnsExactMapBytes()
    {
        byte[] fixture = LhmSharedMemoryReaderTests.BuildSensorsMapFixture();
        using var map = MemoryMappedFile.CreateNew(null, fixture.Length);
        using (var writer = map.CreateViewAccessor())
        {
            writer.WriteArray(0, fixture, 0, fixture.Length);
        }

        var source = new MemoryMappedLhmMapSource(
            openMutex: _ => new Mutex(),
            openMap: _ => map);

        byte[]? bytes = source.TryReadSensorsMap(out string? error);

        Assert.IsNull(error);
        Assert.IsNotNull(bytes);
        CollectionAssert.AreEqual(fixture, bytes, "the copy must return exactly the declared data extent");
    }
}
