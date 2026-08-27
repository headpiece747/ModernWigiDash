using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.Tests;

/// <summary>
/// The key-capture editor's WPF mapper pinned (the pure model's rules stay
/// in KeyCaptureModelTests): the ChordKeyName mapping (the chord
/// vocabulary's main keys map to a name; a numpad, modifier, symbol, or
/// the raw System key maps to null, so the capture cannot record a chord
/// the vocabulary would refuse or one key that registers another), and
/// the editor glue on a live STA window driven with synthesized key
/// events: the click arms the capture (a box that cannot take focus
/// cancels it instead of arming a zombie), a modifier-less press is
/// swallowed and stays armed, a System press resolves to the real key,
/// Escape cancels and is handled, a focus loss cancels, and a cancelled
/// capture swallows no more presses.
/// </summary>
[TestClass]
public class KeyCaptureEditorTests
{
    private const string StoredChord = "Ctrl+P";

    private static readonly StaHost Host = new("KeyCaptureEditor-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    // --- ChordKeyName: the key-name mapping ---

    [TestMethod]
    public void ChordKeyName_LettersDigitsAndFunctionKeys_MapToChordNames()
    {
        Assert.AreEqual("A", InspectorPanelRenderer.ChordKeyName(Key.A));
        Assert.AreEqual("M", InspectorPanelRenderer.ChordKeyName(Key.M));
        Assert.AreEqual("Z", InspectorPanelRenderer.ChordKeyName(Key.Z));
        Assert.AreEqual("0", InspectorPanelRenderer.ChordKeyName(Key.D0));
        Assert.AreEqual("7", InspectorPanelRenderer.ChordKeyName(Key.D7));
        Assert.AreEqual("9", InspectorPanelRenderer.ChordKeyName(Key.D9));
        Assert.AreEqual("F1", InspectorPanelRenderer.ChordKeyName(Key.F1));
        Assert.AreEqual("F12", InspectorPanelRenderer.ChordKeyName(Key.F12));
        Assert.AreEqual("F24", InspectorPanelRenderer.ChordKeyName(Key.F24));
    }

    [TestMethod]
    public void ChordKeyName_NumpadKeys_MapToNull_TheVirtualKeysDiffer()
    {
        // The numpad digits share the number row's names but carry distinct
        // virtual keys: mapping them would compose a chord that spells one
        // key while registering another.
        foreach (Key numpad in new[] { Key.NumPad0, Key.NumPad5, Key.NumPad9 })
            Assert.IsNull(InspectorPanelRenderer.ChordKeyName(numpad), $"{numpad} must map to null");
    }

    [TestMethod]
    public void ChordKeyName_ModifiersSymbolsAndTheSystemKey_MapToNull()
    {
        foreach (Key modifier in new[] { Key.LeftShift, Key.LeftCtrl, Key.LeftAlt, Key.LWin, Key.RWin, Key.CapsLock, Key.NumLock, Key.Scroll })
            Assert.IsNull(InspectorPanelRenderer.ChordKeyName(modifier), $"{modifier} must map to null");
        foreach (Key other in new[] { Key.OemSemicolon, Key.OemPlus, Key.OemMinus, Key.Space, Key.Enter, Key.Tab, Key.None, Key.System, Key.Escape })
            Assert.IsNull(InspectorPanelRenderer.ChordKeyName(other), $"{other} must map to null");
    }

    // --- The editor glue, driven on a live STA window ---

    [TestMethod]
    public void Click_ArmsTheCaptureAndSeedsTheBoxFromTheStoredChord()
    {
        Host.Run<object?>(() =>
        {
            var (window, _, button, box, model) = BuildHostedEditor();
            try
            {
                Assert.AreEqual(StoredChord, box.Text, "the box seeds from the stored chord");
                Click(button);

                Assert.IsTrue(model.IsCapturing, "the click arms the capture on a box that can take focus");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
    }

    [TestMethod]
    public void BareKeyPress_IsSwallowedAndStaysArmed()
    {
        Host.Run<object?>(() =>
        {
            var (window, _, button, box, model) = BuildHostedEditor();
            try
            {
                Click(button);

                KeyEventArgs press = KeyPress(box, Key.C);
                box.RaiseEvent(press);

                Assert.IsTrue(press.Handled, "every press during capture stays out of the box");
                Assert.IsTrue(model.IsCapturing, "the model's no-modifier refusal keeps the capture armed");
                Assert.AreEqual(StoredChord, box.Text, "a refused press changes nothing");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
    }

    [TestMethod]
    public void ResolvePressKey_ASystemPress_RoutesToTheSystemKey()
    {
        // Alt (and Win) combo presses arrive as Key.System carrying the
        // real key in the event's system key: without the resolution an
        // Alt chord is unrecordable (the System key maps to no name).
        // The press's Key/SystemKey pair is read-only in WPF (no public
        // synthesis), so the policy is pinned pure against its inputs.
        Assert.AreEqual(Key.C, InspectorPanelRenderer.ResolvePressKey(Key.System, Key.C));
        Assert.AreEqual(Key.F1, InspectorPanelRenderer.ResolvePressKey(Key.System, Key.F1));
        // A non-System primary passes through untouched: the system key
        // is the alternate source only.
        Assert.AreEqual(Key.C, InspectorPanelRenderer.ResolvePressKey(Key.C, Key.System));
        Assert.AreEqual(Key.None, InspectorPanelRenderer.ResolvePressKey(Key.System, Key.None));
    }

    [TestMethod]
    public void Escape_CancelsTheCaptureAndIsHandled()
    {
        Host.Run<object?>(() =>
        {
            var (window, _, button, box, model) = BuildHostedEditor();
            try
            {
                Click(button);

                KeyEventArgs escape = KeyPress(box, Key.Escape);
                box.RaiseEvent(escape);
                Assert.IsTrue(escape.Handled);
                Assert.IsFalse(model.IsCapturing, "Escape cancels the capture");

                // A cancelled capture swallows no more presses: the handler
                // early-outs before touching the event.
                KeyEventArgs after = KeyPress(box, Key.C);
                box.RaiseEvent(after);
                Assert.IsFalse(after.Handled, "a cancelled capture leaves the press to the box's normal routing");
                Assert.AreEqual(StoredChord, box.Text);
            }
            finally
            {
                window.Close();
            }
            return null;
        });
    }

    [TestMethod]
    public void LostFocus_CancelsTheCapture()
    {
        Host.Run<object?>(() =>
        {
            var (window, _, button, box, model) = BuildHostedEditor();
            try
            {
                Click(button);
                Assert.IsTrue(model.IsCapturing);

                box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, box));

                Assert.IsFalse(model.IsCapturing, "a focus loss cancels the capture (the armed box must never capture in the background)");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
    }

    [TestMethod]
    public void Click_OnADetachedEditor_CancelsTheFailedFocus()
    {
        Host.Run<object?>(() =>
        {
            // No window: the box cannot take focus, so the click must cancel
            // the capture instead of arming a zombie (a focus loss can never
            // fire on an unfocusable box).
            var (_, button, _, model) = BuildDetachedEditor();
            Click(button);

            Assert.IsFalse(model.IsCapturing, "a failed focus cancels the armed capture");
            return null;
        });
    }

    /// <summary>Builds the real editor into a shown + activated window and
    /// returns the teardown-ordered parts (window, row, button, box, model).</summary>
    private static (Window Window, UIElement Editor, Button Button, TextBox Box, KeyCaptureModel Model) BuildHostedEditor()
    {
        var (editor, button, box, model) = BuildDetachedEditor();
        var window = new Window { Content = editor };
        WpfWindow.ShowOwner(window);
        window.Activate();
        window.UpdateLayout();
        return (window, editor, button, box, model);
    }

    /// <summary>Builds the real editor detached from any window (the
    /// focus-failure arm) and digs the button/box out of the row.</summary>
    private static (UIElement Editor, Button Button, TextBox Box, KeyCaptureModel Model) BuildDetachedEditor()
    {
        var desc = new EditorDescription(
            typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.GlobalHotkey))!,
            "Global Hotkey",
            WidgetPropertyType.Text,
            StoredChord,
            [],
            false);
        var callbacks = new InspectorCallbacks
        {
            TryFindResource = _ => null,
            ApplyInspectorPropertyValue = (_, _) => { },
            ShowIconSelectorPopup = (_, _, _) => { },
            AttachDropdownWithinWindow = _ => { },
            BrowseFile = (_, _) => null,
            BrowseFolder = _ => null,
        };
        var (editor, model) = InspectorPanelRenderer.BuildKeyCaptureEditor(desc, callbacks);
        var row = (DockPanel)editor;
        var button = (Button)row.Children[0];
        var box = (TextBox)row.Children[1];
        return (editor, button, box, model);
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    /// <summary>Synthesizes a PreviewKeyDown press on the box: the only public
    /// KeyEventArgs ctor leaves RoutedEvent unset (a WPF internal shape), so
    /// the helper patches it before the raise (the setter throws only while
    /// an event is mid-route, which a fresh instance never is).</summary>
    private static KeyEventArgs KeyPress(TextBox box, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(box), 0, key);
        press.RoutedEvent = UIElement.PreviewKeyDownEvent;
        return press;
    }
}
