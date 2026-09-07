namespace ModernWigiDash.App;

/// <summary>
/// The named verdicts of the manual profile export flow
/// (<see cref="ProfileExportFlow"/> is the only producer): the bundle was
/// written to the chosen path (the success line surfaced), or the write failed
/// (the error line surfaced). Pattern match on the nested cases.
/// </summary>
internal abstract record ProfileExportFlowOutcome
{
    private ProfileExportFlowOutcome()
    {
    }

    /// <summary>The export bundle (profile + theme section) was written to the
    /// chosen path and the success line was surfaced.</summary>
    public sealed record Exported : ProfileExportFlowOutcome;

    /// <summary>The write failed (a permission fault, a full disk, a delete
    /// between the dialog and the write); <see cref="Detail"/> is the exception
    /// message and the flow surfaced the error line.</summary>
    public sealed record Failed(string Detail) : ProfileExportFlowOutcome;
}
