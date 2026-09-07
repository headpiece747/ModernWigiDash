namespace ModernWigiDash.Sdk;

/// <summary>
/// The script-launch facet of the widget host context: the member a widget
/// needs to spawn a user-supplied AutoHotkey script (the hotkey widget's
/// "Run AHK Script" action, ADR-0019). Split from
/// <see cref="IModernWigiDashContext"/> so a widget that launches scripts
/// depends on this capability explicitly instead of seeing every host service
/// (the <c>IWidgetActionInvoker</c> / <c>IWidgetEditorProvider</c> optional-facet
/// precedent). A host without an interpreter simply does not implement it;
/// <see cref="ModernWidgetBase.ScriptLaunchContext"/> is then null and the
/// launch degrades to a no-op.
/// </summary>
public interface IWidgetScriptLaunchContext
{
    /// <summary>
    /// Launches the named AutoHotkey script with the user's own interpreter
    /// (ADR-0019: the interpreter path is machine-local, user-supplied in the
    /// settings; the app bundles nothing). The host owns the kill-switch veto
    /// and the refusal lines (a checked kill switch, an unset or missing
    /// interpreter); the App's context resolves the interpreter from its
    /// machine-local settings and spawns.
    /// </summary>
    /// <param name="scriptPath">The .ahk script path to launch.</param>
    void LaunchAutoHotkeyScript(string scriptPath);
}
