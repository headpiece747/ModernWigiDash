namespace ModernWigiDash.Widgets;

/// <summary>
/// Optional widget capability: the widget can own a global (OS-level) hotkey.
/// The App's window discovers the capability (the <see cref="IWidgetEditorProvider"/>
/// precedent: an optional interface, no widget-type checks), registers the
/// chord on the window handle, and routes the OS's WM_HOTKEY to
/// <see cref="FireGlobalHotkey"/>. The chord vocabulary (what text is a valid
/// chord) stays with the Widgets layer: the provider parses the stored text
/// into the RegisterHotKey operands, so the host never parses a chord itself
/// (the parse vocabulary is internal to Widgets).
/// </summary>
public interface IGlobalHotkeyProvider
{
    /// <summary>
    /// The widget's global hotkey parsed into its RegisterHotKey operands:
    /// the modifier flags (the GlobalHotkeyChordPolicy MOD vocabulary) and
    /// the single main key's virtual-key code, plus the chord's stored
    /// spelling (the host's log lines). False when the widget wants no
    /// global hotkey (a blank chord) or the chord is unparseable (a
    /// modifier-less or duplicate-modifier chord, an unknown key).
    /// </summary>
    /// <param name="modFlags">The modifier flags (MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN).</param>
    /// <param name="virtualKey">The main key's virtual-key code.</param>
    /// <param name="chord">The chord's stored spelling (for log lines).</param>
    bool TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord);

    /// <summary>
    /// Fires the widget's action from the OS hotkey. The same entry point as
    /// a touch-up, so the widget's re-entrancy gate, timeout, and failure
    /// logging apply to both triggers.
    /// </summary>
    void FireGlobalHotkey();

    /// <summary>
    /// Whether writing the named property changes this widget's registered
    /// global-hotkey chord (so the host should re-run its idempotent
    /// registration pass). The decision lives with the provider because only
    /// it knows which of its properties is the chord; the host's commit owner
    /// stays free of per-widget property names. Default false: a provider that
    /// does not override it has no chord-affecting property.
    /// </summary>
    /// <param name="propertyName">The property being committed.</param>
    bool AffectsGlobalHotkey(string propertyName) => false;
}
