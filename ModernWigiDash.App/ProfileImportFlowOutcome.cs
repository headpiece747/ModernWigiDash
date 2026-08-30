namespace ModernWigiDash.App;

/// <summary>
/// The named verdicts of the manual profile import flow
/// (<see cref="ProfileImportFlow"/> is the only producer): the imported
/// profile was swapped in (with the bundle's theme restored behind the
/// user's confirm, or not); the swap itself failed; or the boundary's
/// rejection verdicts (too large, unparseable) and the silent absent-file
/// no-op. Pattern match on the nested cases.
/// </summary>
internal abstract record ProfileImportFlowOutcome
{
    private ProfileImportFlowOutcome()
    {
    }

    /// <summary>The imported profile was swapped in. <see cref="ThemeRestored"/>
    /// says whether the bundle's theme item was confirmed and applied as
    /// well (a declined or unoffered theme keeps the machine's theme).</summary>
    public sealed record Imported(bool ThemeRestored) : ProfileImportFlowOutcome;

    /// <summary>The profile swap failed (the host surfaced the error line);
    /// the bundle's theme was never offered, so a failed import never
    /// applies the theme of a profile that was not applied.</summary>
    public sealed record SwapFailed : ProfileImportFlowOutcome;

    /// <summary>The boundary rejected the file as oversized (before any
    /// read); the flow surfaced the error line.</summary>
    public sealed record TooLarge : ProfileImportFlowOutcome;

    /// <summary>The file read but no profile could be parsed; <see cref="Detail"/>
    /// is the boundary's caller-facing reason and the flow surfaced the
    /// error line.</summary>
    public sealed record Failed(string Detail) : ProfileImportFlowOutcome;

    /// <summary>No file at the path: a delete between the dialog and the read
    /// is a silent no-op, the file the dialog handed back is gone.</summary>
    public sealed record Absent : ProfileImportFlowOutcome;
}
