using ModernWigiDash.App.Hotkey;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's global-hotkey manager pinned through the fake <see
/// cref="HotkeyApi"/>: the register diff (added cells register, removed
/// cells unregister, held cells keep their ids), the MOD_NOREPEAT added at
/// the OS boundary, the foreign-owned refusal (untracked + one log line per
/// cell per session), the WM_HOTKEY routing to the owning widget, and the
/// teardown release.
/// </summary>
[TestClass]
public class GlobalHotkeyManagerTests
{
    private static readonly IntPtr Handle = new(0xCAFE);

    private sealed class FakeApi
    {
        public record Registration(IntPtr Hwnd, int Id, int Mod, ushort Vk);
        public record Unregistration(IntPtr Hwnd, int Id, ushort Vk);

        public List<Registration> Registered { get; } = [];
        public List<Unregistration> Unregistered { get; } = [];
        public HashSet<(int Mod, ushort Vk)> RefuseCells { get; } = [];

        public HotkeyApi Api { get; }

        // The fake's delegates capture the recorder fields, so the HotkeyApi
        // is built in the constructor body (a field initializer cannot
        // reference the sibling fields, even inside the lambdas).
        public FakeApi()
        {
            Api = new HotkeyApi(
                (handle, id, mod, vk) =>
                {
                    if (RefuseCells.Contains((mod, vk))) return false;
                    Registered.Add(new Registration(handle, id, mod, vk));
                    return true;
                },
                (handle, id, vk) => Unregistered.Add(new Unregistration(handle, id, vk)));
        }
    }

    private sealed class FakeProvider : IGlobalHotkeyProvider
    {
        public int Fires { get; private set; }
        public bool TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord)
        {
            modFlags = 0;
            virtualKey = 0;
            chord = "";
            return false;
        }
        public void FireGlobalHotkey() => Fires++;
    }

    private static DesiredGlobalHotkey Candidate(FakeProvider owner, int flags, ushort vk, string chord)
        => new(flags, vk, chord, owner);

    private static (GlobalHotkeyManager Manager, FakeApi Fake, List<string> LogLines) CreateManager()
    {
        var logLines = new List<string>();
        var fake = new FakeApi();
        var manager = new GlobalHotkeyManager(fake.Api, new DiagLog("HOTKEY", 1, write: logLines.Add));
        return (manager, fake, logLines);
    }

    [TestMethod]
    public void Refresh_RegistersTheDesiredCells_WithNoRepeatAtTheOsBoundary()
    {
        var (manager, fake, _) = CreateManager();
        var owner = new FakeProvider();
        var candidate = Candidate(owner, 0x3, (ushort)'X', "Ctrl+Alt+X");

        manager.Refresh(Handle, [candidate]);

        var registration = fake.Registered.Single();
        Assert.AreEqual(Handle, registration.Hwnd);
        Assert.AreEqual(0x4003, registration.Mod,
            "the MOD_NOREPEAT flag (Win32 0x4000) is ORed at the OS boundary");
        Assert.AreEqual((ushort)'X', registration.Vk);
        Assert.IsTrue(manager.Fire(registration.Id), "a registered id routes");
        Assert.AreEqual(1, owner.Fires, "the WM_HOTKEY id routes to the owning widget's fire path");
    }

    [TestMethod]
    public void Refresh_Idempotent_ASecondPassOfTheSameSetIsANoOp()
    {
        var (manager, fake, _) = CreateManager();
        var candidate = Candidate(new FakeProvider(), 0x2, (ushort)'A', "Ctrl+A");

        manager.Refresh(Handle, [candidate]);
        int firstId = fake.Registered.Single().Id;
        manager.Refresh(Handle, [candidate]);

        Assert.AreEqual(1, fake.Registered.Count, "a held cell keeps its registration (no OS churn)");
        Assert.AreEqual(firstId, fake.Registered.Single().Id, "the id is stable across refreshes");
        Assert.AreEqual(0, fake.Unregistered.Count);
    }

    [TestMethod]
    public void Refresh_ChangedSet_UnregistersTheRemoved_KeepsTheHeldRegistersTheAdded()
    {
        var (manager, fake, _) = CreateManager();
        var a = Candidate(new FakeProvider(), 0x2, (ushort)'A', "Ctrl+A");
        var b = Candidate(new FakeProvider(), 0x4, (ushort)'B', "Shift+B");
        var c = Candidate(new FakeProvider(), 0x8, (ushort)'C', "Win+C");

        manager.Refresh(Handle, [a, b]);
        int bId = fake.Registered.Single(r => r.Vk == (ushort)'B').Id;
        manager.Refresh(Handle, [b, c]);

        Assert.AreEqual(3, fake.Registered.Count, "A and B registered, then C");
        Assert.AreEqual(1, fake.Unregistered.Count, "only A left the desired set");
        Assert.AreEqual((ushort)'A', fake.Unregistered.Single().Vk);
        Assert.AreEqual(bId, fake.Registered.Single(r => r.Vk == (ushort)'B').Id, "the held cell B keeps its id");
    }

    [TestMethod]
    public void Refresh_ForeignOwnedCell_StaysUntracked_AndLogsOncePerCellPerSession()
    {
        var (manager, fake, logLines) = CreateManager();
        var refused = Candidate(new FakeProvider(), 0x2, (ushort)'A', "Ctrl+A");
        fake.RefuseCells.Add((0x4002, (ushort)'A'));

        manager.Refresh(Handle, [refused]);
        manager.Refresh(Handle, [refused]);

        Assert.AreEqual(0, fake.Registered.Count, "a refused cell is never tracked");
        Assert.IsFalse(manager.Fire(0x574D0001), "an untracked cell's id is not routable");
        CollectionAssert.AreEqual(
            new[] { "[HOTKEY] Global hotkey Ctrl+A is owned by another program; this widget's hotkey is inert (tapping still works)" },
            logLines,
            "the refusal logs one line per cell per session, not one per refresh");
    }

    [TestMethod]
    public void Refresh_ZeroHandle_ClearsEveryRegistration()
    {
        var (manager, fake, _) = CreateManager();
        manager.Refresh(Handle, [Candidate(new FakeProvider(), 0x2, (ushort)'A', "Ctrl+A")]);
        int id = fake.Registered.Single().Id;

        manager.Refresh(IntPtr.Zero, []);

        Assert.AreEqual(1, fake.Unregistered.Count);
        Assert.AreEqual(id, fake.Unregistered.Single().Id);
        Assert.IsFalse(manager.Fire(id), "after the clear the id is not routable");
    }

    [TestMethod]
    public void Dispose_UnregistersEverythingOnTheLastHandle()
    {
        var (manager, fake, _) = CreateManager();
        manager.Refresh(Handle, [Candidate(new FakeProvider(), 0x2, (ushort)'A', "Ctrl+A")]);
        int id = fake.Registered.Single().Id;

        manager.Dispose();

        Assert.AreEqual(1, fake.Unregistered.Count);
        Assert.AreEqual(Handle, fake.Unregistered.Single().Hwnd);
        Assert.AreEqual(id, fake.Unregistered.Single().Id);
    }

    [TestMethod]
    public void Fire_UnknownId_ReturnsFalseAndFiresNothing()
    {
        var (manager, _, _) = CreateManager();
        var owner = new FakeProvider();

        Assert.IsFalse(manager.Fire(0x574D0001));
        Assert.AreEqual(0, owner.Fires);
    }
}
