using System.Windows.Input;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// The inspector's key-capture editor rules (the LocationSearchModel
/// precedent: the pure decision model the thin WPF mapper drives): the chord
/// text the capture records (the house spelling "Ctrl+Shift+F1" - the same
/// plus-separated shape the chord vocabulary validates) and the capture's
/// verdict. The composer orders the modifiers Ctrl, Alt, Shift, Win. A key
/// with no modifier is refused (a modifier-less global hotkey would shadow
/// the key system-wide), a blank or modifier key as the main key is refused,
/// and a refusal leaves the capture armed so the user presses again.
/// </summary>
internal sealed class KeyCaptureModel(string chord = "")
{
    private readonly Lock _gate = new();
    private string _chord = chord;
    private bool _capturing;

    /// <summary>The captured chord's stored spelling (empty = none).</summary>
    public string Chord
    {
        get { lock (_gate) return _chord; }
    }

    /// <summary>Whether the editor is waiting for a key press.</summary>
    public bool IsCapturing
    {
        get { lock (_gate) return _capturing; }
    }

    /// <summary>Arms the capture (the editor's "Press keys" affordance).</summary>
    public void BeginCapture()
    {
        lock (_gate) _capturing = true;
    }

    /// <summary>Stops the capture without recording (a cancel).</summary>
    public void CancelCapture()
    {
        lock (_gate) _capturing = false;
    }

    /// <summary>
    /// Records the pressed key: the modifier states + the key name compose
    /// the chord. Returns true when the press is consumed (recorded, the
    /// capture stops). A press without any modifier, a blank key name, or a
    /// modifier key as the main key is refused: the chord keeps its previous
    /// value and the capture stays armed.
    /// </summary>
    /// <param name="keyName">The main key's name ("A", "F1", "5", ...).</param>
    /// <param name="control">Whether Ctrl was held.</param>
    /// <param name="alt">Whether Alt was held.</param>
    /// <param name="shift">Whether Shift was held.</param>
    /// <param name="win">Whether the Win key was held.</param>
    public bool CaptureKey(string keyName, bool control, bool alt, bool shift, bool win)
    {
        lock (_gate)
        {
            if (!_capturing) return false;
            if (string.IsNullOrWhiteSpace(keyName)) return false;
            if (ModifierKeyNames.Contains(keyName)) return false;
            if (!control && !alt && !shift && !win) return false;

            _chord = ComposeChord(keyName, control, alt, shift, win);
            _capturing = false;
            return true;
        }
    }

    /// <summary>
    /// Composes the chord's house spelling: the present modifiers in the
    /// Ctrl, Alt, Shift, Win order + the main key name.
    /// </summary>
    /// <param name="keyName">The main key's name.</param>
    /// <param name="control">Whether Ctrl was held.</param>
    /// <param name="alt">Whether Alt was held.</param>
    /// <param name="shift">Whether Shift was held.</param>
    /// <param name="win">Whether the Win key was held.</param>
    internal static string ComposeChord(string keyName, bool control, bool alt, bool shift, bool win)
    {
        var parts = new List<string>(5);
        if (control) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    // A modifier key as the main key composes a chord with no key to fire on
    // (a "Ctrl+Ctrl" the vocabulary would refuse): the capture vetoes it.
    private static readonly HashSet<string> ModifierKeyNames =
    [
        "Ctrl", "Control", "LControl", "RControl",
        "Alt", "LAlt", "RAlt",
        "Shift", "LShift", "RShift",
        "Win", "LWin", "RWin"
    ];

    /// <summary>
    /// Resolves the pressed key: a System press routes to the event's system
    /// key (the real key held alongside AltGr/Win), every other primary passes
    /// through untouched. Display-free: the WPF mapper drives this from the
    /// key event's operands.
    /// </summary>
    internal static Key ResolvePressKey(Key key, Key systemKey) =>
        key == Key.System ? systemKey : key;

    /// <summary>
    /// The capture's key-name mapping: the chord vocabulary's main keys only
    /// (letters, digits, F1-F24) map to a name; everything else (a symbol,
    /// a modifier, a numpad key) is null, so the capture cannot record a
    /// chord the vocabulary would refuse or that would spell one key while
    /// registering another (the numpad digits share the number row's names
    /// but carry distinct virtual keys).
    /// </summary>
    internal static string? ChordKeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        _ => null
    };
}
