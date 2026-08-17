namespace ModernWigiDash.App;

/// <summary>
/// The shape of a profile mutation — the single input to the window's
/// post-mutation contract (<c>MainWindow.ApplyProfileMutation</c>). The shape
/// selects the refresh bundle, and every shape ends with exactly one
/// dirty-mark, so no call site re-derives "what happens after a mutation":
/// <list type="bullet">
/// <item><see cref="Transform"/> — in-page state only (placing/removing/clearing
/// widgets, transform or opacity write-backs, snap-to-grid, page background):
/// the selection is re-applied, the count and canvas refreshed, no
/// structural re-sync.</item>
/// <item><see cref="Structural"/> — the page set changed (add/delete/rename/
/// switch): the tab strip and page-background picker are re-synced from the
/// profile, then the same tail as <see cref="Transform"/>.</item>
/// <item><see cref="RawWrite"/> — the profile state was replaced wholesale
/// (import): everything <see cref="Structural"/> does, plus the snap-to-grid
/// toggle is re-synced from the imported page, so a raw write can never
/// strand a stale control state.</item>
/// </list>
/// </summary>
internal enum ProfileMutationShape
{
    Transform,
    Structural,
    RawWrite,
}
