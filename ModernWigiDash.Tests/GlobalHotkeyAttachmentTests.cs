using ModernWigiDash.App.Hotkey;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The global-hotkey attachment module pinned without WPF: the WM_HOTKEY
/// message routing (a registered id marks the message handled and routes to
/// the owning widget's fire path; an unknown id leaves it for the default
/// handler), the zero-handle guard (a pre-attach refresh is a no-op), and
/// the teardown release (the dispose releases every registration and the
/// released ids stay unroutable).
/// </summary>
[TestClass]
public class GlobalHotkeyAttachmentTests
{
    private static readonly IntPtr Handle = new(0xBEEF);

    private sealed class FakeApi
    {
        public List<int> RegisteredIds { get; } = [];
        public List<int> UnregisteredIds { get; } = [];

        public HotkeyApi Api { get; }

        public FakeApi()
        {
            Api = new HotkeyApi(
                (_, id, _, _) => { RegisteredIds.Add(id); return true; },
                (_, id, _) => UnregisteredIds.Add(id));
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

    private static (GlobalHotkeyAttachment Attachment, FakeApi Fake) CreateAttachment()
    {
        var fake = new FakeApi();
        var attachment = new GlobalHotkeyAttachment(fake.Api, new DiagLog("HOTKEY", 1, write: _ => { }));
        return (attachment, fake);
    }

    [TestMethod]
    public void OnHotkeyMessage_RegisteredId_RoutesToTheOwnerAndMarksHandled()
    {
        var (attachment, fake) = CreateAttachment();
        var owner = new FakeProvider();
        attachment.RegisterForTest(Handle, [new DesiredGlobalHotkey(0x2, (ushort)'A', "Ctrl+A", owner)]);
        int id = fake.RegisteredIds.Single();

        bool handled = false;
        attachment.OnHotkeyMessage(0x0312, (IntPtr)id, ref handled);

        Assert.IsTrue(handled, "a registered id marks the message handled");
        Assert.AreEqual(1, owner.Fires, "the WM_HOTKEY id routes to the owning widget's fire path");
    }

    [TestMethod]
    public void OnHotkeyMessage_UnknownId_LeavesTheMessageForTheDefaultHandler()
    {
        var (attachment, _) = CreateAttachment();
        var owner = new FakeProvider();
        attachment.RegisterForTest(Handle, [new DesiredGlobalHotkey(0x2, (ushort)'A', "Ctrl+A", owner)]);

        bool handled = false;
        attachment.OnHotkeyMessage(0x0312, (IntPtr)0x574DFFFF, ref handled);

        Assert.IsFalse(handled, "an unknown id leaves the message for the default handler");
        Assert.AreEqual(0, owner.Fires, "an unknown id fires nothing");
    }

    [TestMethod]
    public void Refresh_BeforeAttach_IsANoOp()
    {
        var (attachment, fake) = CreateAttachment();

        attachment.Refresh(() => [], () => false);

        Assert.AreEqual(0, fake.RegisteredIds.Count, "a zero handle (pre-attach) is a no-op");
    }

    [TestMethod]
    public void Dispose_ReleasesEveryRegistration_AndTheHandleGuardStopsFurtherPasses()
    {
        var (attachment, fake) = CreateAttachment();
        var owner = new FakeProvider();
        attachment.RegisterForTest(Handle, [new DesiredGlobalHotkey(0x2, (ushort)'A', "Ctrl+A", owner)]);
        int id = fake.RegisteredIds.Single();

        attachment.Dispose();
        attachment.Refresh(() => [new DesiredGlobalHotkey(0x4, (ushort)'B', "Shift+B", owner)], () => false);

        CollectionAssert.Contains(fake.UnregisteredIds, id, "the dispose releases the registration on the last handle");
        Assert.AreEqual(1, fake.RegisteredIds.Count, "a post-teardown refresh is a no-op (the handle is zeroed)");
        bool handled = false;
        attachment.OnHotkeyMessage(0x0312, (IntPtr)id, ref handled);
        Assert.IsFalse(handled, "a released id is not routable");
    }
}
