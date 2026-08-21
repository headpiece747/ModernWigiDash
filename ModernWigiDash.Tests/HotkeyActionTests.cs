using System.Text.Json;

namespace ModernWigiDash.Tests;

[TestClass]
public class HotkeyActionTests
{
    [TestMethod]
    public void MediaKeyCatalog_ListsSevenActionsWithFriendlyNames()
    {
        Assert.AreEqual(7, MediaKeyCatalog.Options.Count);
        Assert.AreEqual("PLAYPAUSE", MediaKeyCatalog.Options[0].Value);
        Assert.AreEqual("Play / Pause", MediaKeyCatalog.Options[0].DisplayName);
        Assert.AreEqual("Stop", MediaKeyCatalog.GetDisplayName("STOP"));
        Assert.IsNull(MediaKeyCatalog.GetDisplayName("BOGUS"));
        Assert.AreEqual("Volume up", MediaKeyCatalog.Options[4].DisplayName);
    }

    [TestMethod]
    public void HotkeyAction_MediaKeySummary_UsesFriendlyName()
    {
        var action = new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "VOLUMEUP" };
        Assert.AreEqual("Media: Volume up", action.Summary());
        Assert.AreEqual("Media: CUSTOMKEY", new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "CUSTOMKEY" }.Summary());
    }

    [TestMethod]
    public void ParseVirtualKey_MediaKeys_IncludeStop()
    {
        Assert.AreEqual(0xB2, (int)HotkeyActionExecutor.ParseVirtualKey("STOP"));
        Assert.AreEqual(0xB3, (int)HotkeyActionExecutor.ParseVirtualKey("PLAYPAUSE"));
        Assert.AreEqual(0xB0, (int)HotkeyActionExecutor.ParseVirtualKey("NEXT"));
        Assert.AreEqual(0xB1, (int)HotkeyActionExecutor.ParseVirtualKey("PREVIOUS"));
        Assert.AreEqual(0xAD, (int)HotkeyActionExecutor.ParseVirtualKey("MUTE"));
        Assert.AreEqual(0xAE, (int)HotkeyActionExecutor.ParseVirtualKey("VOLUMEDOWN"));
        Assert.AreEqual(0xAF, (int)HotkeyActionExecutor.ParseVirtualKey("VOLUMEUP"));
    }

    [TestMethod]
    public void HotkeyAction_Summary_DescribesConfiguredAction()
    {
        Assert.AreEqual("Launch calc.exe", new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = "calc.exe" }.Summary());
        Assert.AreEqual("Wait 100 ms", new HotkeyAction { Kind = HotkeyActionKind.Delay, DelayMs = 100 }.Summary());
    }

    [TestMethod]
    public void HotkeyActions_SerializeAndRoundTrip()
    {
        List<HotkeyAction> actions =
        [
            new() { Kind = HotkeyActionKind.KeyChord, Value = "Ctrl+Shift+S", DelayMs = 50 },
            new() { Kind = HotkeyActionKind.Text, Value = "Hello", Repeat = 2 },
            new() { Kind = HotkeyActionKind.Delay, DelayMs = 250 }
        ];

        string json = JsonSerializer.Serialize(actions);
        var roundTrip = JsonSerializer.Deserialize<List<HotkeyAction>>(json);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(actions.Count, roundTrip.Count);
        Assert.AreEqual("Ctrl+Shift+S", roundTrip[0].Value);
        Assert.AreEqual(HotkeyActionKind.Delay, roundTrip[2].Kind);
        Assert.AreEqual(250, roundTrip[2].DelayMs);
    }
}
