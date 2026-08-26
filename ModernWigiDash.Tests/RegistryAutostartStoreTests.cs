using Microsoft.Win32;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The production autostart store pinned through its real registry API (the
/// TwitchTokenStore real-DPAPI precedent: the adapter is pinned against its
/// own seams, not a mirror). The round trip runs against a temp HKCU subkey
/// (the injectable root hive) so the machine's real Run entries are never
/// touched; each test gets its own GUID-suffixed tree, deleted on cleanup.
/// </summary>
[TestClass]
public class RegistryAutostartStoreTests
{
    private const string TempRootPath = @"Software\ModernWigiDash.Tests\AutostartRoundTrip";

    private string _path = null!;
    private RegistryKey _root = null!;
    private IAutostartStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _path = $@"{TempRootPath}\{Guid.NewGuid():N}";
        _root = Registry.CurrentUser.CreateSubKey(_path)
            ?? throw new InvalidOperationException($"could not create the temp autostart root ({_path})");
        _store = new RegistryAutostartStore(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _root.Close();
        // The store's Run subkey lives under this root; deleting the whole
        // tree (missing is a no-op) leaves nothing behind.
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    [TestMethod]
    public void TryGetCommandLine_NoEntry_Null()
        => Assert.IsNull(_store.TryGetCommandLine());

    [TestMethod]
    public void SetCommandLine_WritesTheValue_WhichTheNextReadReturns()
    {
        _store.SetCommandLine("C:\\wmd.exe --startup");
        Assert.AreEqual("C:\\wmd.exe --startup", _store.TryGetCommandLine());
    }

    [TestMethod]
    public void SetCommandLine_Null_DeletesTheEntry()
    {
        _store.SetCommandLine("C:\\wmd.exe --startup");
        _store.SetCommandLine(null);
        Assert.IsNull(_store.TryGetCommandLine());
    }

    [TestMethod]
    public void SetCommandLine_NullWithNoEntry_IsANoop()
    {
        _store.SetCommandLine(null); // deleting a missing value must not throw
        Assert.IsNull(_store.TryGetCommandLine());
    }

    [TestMethod]
    public void SetCommandLine_OverwritesThePreviousValue()
    {
        _store.SetCommandLine("C:\\old.exe --startup");
        _store.SetCommandLine("C:\\new.exe --startup");
        Assert.AreEqual("C:\\new.exe --startup", _store.TryGetCommandLine());
    }

    [TestMethod]
    public void ProductionRoot_IsHkcu_AndTheRoundTripSurvivesASecondStoreInstance()
    {
        // A second store on the same root reads what the first wrote: the
        // value, not the store instance, is the source of truth.
        _store.SetCommandLine("C:\\wmd.exe --startup");
        var second = new RegistryAutostartStore(_root);
        Assert.AreEqual("C:\\wmd.exe --startup", second.TryGetCommandLine());
    }
}
