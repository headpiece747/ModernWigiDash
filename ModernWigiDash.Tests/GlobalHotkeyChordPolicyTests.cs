namespace ModernWigiDash.Tests;

/// <summary>
/// The global-hotkey chord policy pinned without the OS: the Win32 MOD
/// vocabulary, the modifier + one-main-key rule (the no-modifier veto, the
/// modifier-only veto, the two-main-key veto, the repeated-modifier veto),
/// the unknown-key veto, and case insensitivity (the chord vocabulary's
/// single owner beside ParseVirtualKey).
/// </summary>
[TestClass]
public class GlobalHotkeyChordPolicyTests
{
    [TestMethod]
    public void TryParseChord_ModifierKeyComposesTheWin32Flags()
    {
        Assert.IsTrue(GlobalHotkeyChordPolicy.TryParseChord("Ctrl+Alt+P", out int flags, out ushort vk));
        Assert.AreEqual(GlobalHotkeyChordPolicy.ModControl | GlobalHotkeyChordPolicy.ModAlt, flags,
            "Ctrl+Alt is MOD_CONTROL|MOD_ALT");
        Assert.AreEqual((ushort)'P', vk);
    }

    [TestMethod]
    public void TryParseChord_EveryModifier_SpellsItsWin32Flag()
    {
        AssertChord("Ctrl+X", GlobalHotkeyChordPolicy.ModControl, 'X');
        AssertChord("Alt+X", GlobalHotkeyChordPolicy.ModAlt, 'X');
        AssertChord("Shift+X", GlobalHotkeyChordPolicy.ModShift, 'X');
        AssertChord("Win+D", GlobalHotkeyChordPolicy.ModWin, 'D');
    }

    [TestMethod]
    public void TryParseChord_CaseAndSpellingVariants_ParseTheSame()
    {
        AssertChord("ctrl+x", GlobalHotkeyChordPolicy.ModControl, 'X');
        AssertChord("CONTROL+X", GlobalHotkeyChordPolicy.ModControl, 'X');
        AssertChord("LControl+X", GlobalHotkeyChordPolicy.ModControl, 'X');
        AssertChord("RAlt+X", GlobalHotkeyChordPolicy.ModAlt, 'X');
        AssertChord("LSHIFT+X", GlobalHotkeyChordPolicy.ModShift, 'X');
        AssertChord("RWIN+X", GlobalHotkeyChordPolicy.ModWin, 'X');
    }

    [TestMethod]
    public void TryParseChord_FunctionAndDigitKeys_ParseToTheirVirtualKeys()
    {
        Assert.IsTrue(GlobalHotkeyChordPolicy.TryParseChord("Ctrl+Shift+F1", out int flags, out ushort vk));
        Assert.AreEqual(GlobalHotkeyChordPolicy.ModControl | GlobalHotkeyChordPolicy.ModShift, flags);
        Assert.AreEqual((ushort)0x70, vk);
        Assert.IsTrue(GlobalHotkeyChordPolicy.TryParseChord("Alt+5", out _, out vk));
        Assert.AreEqual((ushort)'5', vk);
    }

    [TestMethod]
    public void TryParseChord_NoModifier_IsRefused()
    {
        // A modifier-less global hotkey would shadow the key system-wide.
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("F1", out int flags, out _));
        Assert.AreEqual(0, flags);
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("Ctrl+Shift", out _, out _),
            "a modifier-only chord names no key to fire on");
    }

    [TestMethod]
    public void TryParseChord_MalformedChords_AreRefused()
    {
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("", out _, out _));
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("   ", out _, out _));
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("Ctrl+Ctrl+X", out _, out _),
            "a repeated modifier is unparseable");
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("Ctrl+X+Y", out _, out _),
            "two main keys are unparseable");
        Assert.IsFalse(GlobalHotkeyChordPolicy.TryParseChord("Ctrl+Nope", out _, out _),
            "an unknown key is unparseable");
    }

    private static void AssertChord(string chord, int expectedFlags, char expectedKey)
    {
        Assert.IsTrue(GlobalHotkeyChordPolicy.TryParseChord(chord, out int flags, out ushort vk), chord);
        Assert.AreEqual(expectedFlags, flags, chord);
        Assert.AreEqual((ushort)expectedKey, vk, chord);
    }
}
