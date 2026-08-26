using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.Tests;

/// <summary>
/// The inspector's key-capture editor rules pinned against the pure model:
/// the chord composition (the house spelling, the modifier order
/// Ctrl, Alt, Shift, Win), the capture's verdicts (a press without a
/// modifier is refused, a modifier key as the main key is refused, a
/// recorded chord stops the capture, a cancel and an unarmed capture
/// record nothing), and the seed (the model starts from the stored chord).
/// </summary>
[TestClass]
public class KeyCaptureModelTests
{
    [TestMethod]
    public void CaptureKey_WithModifiers_ComposesTheChordInHouseSpelling()
    {
        var model = new KeyCaptureModel();
        model.BeginCapture();

        bool consumed = model.CaptureKey("F1", control: true, alt: false, shift: true, win: false);

        Assert.IsTrue(consumed, "a valid press is consumed");
        Assert.AreEqual("Ctrl+Shift+F1", model.Chord);
        Assert.IsFalse(model.IsCapturing, "a recorded chord stops the capture");
    }

    [TestMethod]
    public void CaptureKey_AllFourModifiers_OrdersCtrlAltShiftWin()
    {
        var model = new KeyCaptureModel();
        model.BeginCapture();

        Assert.IsTrue(model.CaptureKey("A", true, true, true, true));
        Assert.AreEqual("Ctrl+Alt+Shift+Win+A", model.Chord);
    }

    [TestMethod]
    public void CaptureKey_SingleDigitAndWin_SpellsTheChord()
    {
        var model = new KeyCaptureModel();
        model.BeginCapture();

        Assert.IsTrue(model.CaptureKey("5", control: false, alt: false, shift: false, win: true));
        Assert.AreEqual("Win+5", model.Chord);
    }

    [TestMethod]
    public void CaptureKey_NoModifier_IsRefusedAndStaysArmed()
    {
        var model = new KeyCaptureModel();
        model.BeginCapture();

        Assert.IsFalse(model.CaptureKey("A", false, false, false, false),
            "a modifier-less global hotkey would shadow the key system-wide");
        Assert.IsTrue(model.IsCapturing, "the refusal keeps the capture armed");
        Assert.AreEqual("", model.Chord, "a refused press records nothing");
    }

    [TestMethod]
    public void CaptureKey_ModifierKeyAsTheMainKey_IsRefused()
    {
        foreach (string modifierName in new[] { "Ctrl", "Alt", "Shift", "Win", "LControl", "RAlt", "LShift", "RWin" })
        {
            var model = new KeyCaptureModel();
            model.BeginCapture();
            Assert.IsFalse(model.CaptureKey(modifierName, true, false, false, false),
                $"{modifierName} as the main key composes a chord with no key to fire on");
        }
    }

    [TestMethod]
    public void CaptureKey_WithoutBeginCapture_Refuses()
    {
        var model = new KeyCaptureModel();

        Assert.IsFalse(model.CaptureKey("A", true, false, false, false),
            "an unarmed editor records nothing");
        Assert.AreEqual("", model.Chord);
    }

    [TestMethod]
    public void CancelCapture_StopsWithoutRecording()
    {
        var model = new KeyCaptureModel();
        model.BeginCapture();
        model.CancelCapture();

        Assert.IsFalse(model.IsCapturing);
        Assert.IsFalse(model.CaptureKey("A", true, false, false, false));
        Assert.AreEqual("", model.Chord);
    }

    [TestMethod]
    public void Ctor_SeedsTheStoredChord()
    {
        var model = new KeyCaptureModel("Ctrl+Alt+P");

        Assert.AreEqual("Ctrl+Alt+P", model.Chord);
    }

    [TestMethod]
    public void ComposeChord_TheModifierOrderIsCtrlAltShiftWin()
    {
        Assert.AreEqual("Ctrl+Alt+Shift+Win+P", KeyCaptureModel.ComposeChord("P", true, true, true, true));
        Assert.AreEqual("Alt+Q", KeyCaptureModel.ComposeChord("Q", false, true, false, false));
        Assert.AreEqual("Win+M", KeyCaptureModel.ComposeChord("M", false, false, false, true));
    }
}
