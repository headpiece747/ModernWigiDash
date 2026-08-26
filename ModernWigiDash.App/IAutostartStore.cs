namespace ModernWigiDash.App;

/// <summary>
/// The seam behind the Start-with-Windows toggle (ADR-0019): read, write, and
/// delete the app's Run-entry command line. The production adapter
/// (<see cref="RegistryAutostartStore"/>) owns the HKCU mechanics; the window
/// hands its instance to the settings hub, and tests inject an in-memory fake
/// (the <c>SingleInstanceGuard</c> handle-factory seam precedent) or drive the
/// real adapter through a temp HKCU subkey.
/// </summary>
internal interface IAutostartStore
{
    /// <summary>The current command line, or null when no entry exists.</summary>
    string? TryGetCommandLine();

    /// <summary>
    /// Sets the entry (null deletes it). The write is the change: the settings
    /// checkbox commits through here with no Apply step, and the registry is
    /// the state the next seed reads.
    /// </summary>
    void SetCommandLine(string? commandLine);
}
