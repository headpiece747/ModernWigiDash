using ModernWigiDash.Core.Models;

namespace ModernWigiDash.App;

/// <summary>
/// The narrow face of the window the profile-mutation funnel writes to: the
/// structural resync (tab strip), the RawWrite snap-toggle resync (routed
/// through the checkbox's own handler so one source of truth is kept), the
/// active-count text, the canvas repaint request, the single dirty mark, the
/// global-hotkey refresh pass, and the selection re-application (the window's
/// <c>SelectWidget</c>, which owns the same-reference early-out and the
/// compositor/inspector writes). One adapter per effect — adding a
/// post-condition touches this record and the funnel's bundle table, never the
/// window's event-handler surface. Mirrors the <c>TransformFieldBindings</c>
/// image: the window hands the elements in, the module owns the policy.
/// </summary>
internal sealed record ProfileMutationBindings(
    Action<ProfileLayout> RebuildPageTabs,
    Action<bool> SetSnapToGridFromHandler,
    Action WriteActiveCountText,
    Action RequestCanvasRepaint,
    Action MarkDirty,
    Action RefreshGlobalHotkeys,
    Action<PlacedWidgetInstance?> ApplySelection);

/// <summary>
/// The ONE post-mutation contract as a module: whatever shape a mutation takes,
/// its post-conditions run exactly once here, so a call site never re-derives
/// "what happens after a mutation" (refresh bundle, selection re-application,
/// dirty mark). The shape selects the refresh bundle:
/// <see cref="ProfileMutationShape.Structural"/> re-syncs the tab strip (the
/// page set changed); <see cref="ProfileMutationShape.RawWrite"/> (an import)
/// additionally re-syncs the snap-to-grid toggle from the imported page;
/// <see cref="ProfileMutationShape.Transform"/> re-syncs nothing structural
/// (in-page state only). Every shape then re-applies the selection, refreshes
/// the active count, repaints the canvas, and marks the profile dirty exactly
/// once. Inspector-driven write-backs (transform text, opacity, property
/// values) are the one path that marks through the inspector's
/// onProfileChanged callback instead — exactly one invocation per landed
/// write-back, and the window's forwarding handlers add none.
/// </summary>
internal sealed class ProfileMutationFunnel(ProfileMutationBindings bindings)
{
    /// <summary>Apply the shape's refresh bundle plus the shared tail
    /// (selection, count, repaint, single dirty mark, hotkey refresh). The
    /// live profile is handed in so an import swap is seen without the funnel
    /// holding a stale reference.</summary>
    public void Apply(ProfileMutationShape shape, ProfileLayout profile, PlacedWidgetInstance? selection)
    {
        if (shape is ProfileMutationShape.Structural or ProfileMutationShape.RawWrite)
        {
            bindings.RebuildPageTabs(profile);
        }

        if (shape is ProfileMutationShape.RawWrite)
        {
            // A raw write replaces the whole profile state, so the imported
            // page's snap-to-grid may differ from the checkbox's old page's:
            // the resync routes through the checkbox's own handler, which
            // re-derives the profile value from the control and thus keeps one
            // source of truth (no bypass of the write-back loop). On import the
            // handler is wired and idempotently re-enters this same contract
            // with the unchanged value; on the startup resync it is still
            // guarded off by the window's pre-arm state.
            bindings.SetSnapToGridFromHandler(profile.ActivePage.SnapToGrid);
        }

        bindings.ApplySelection(selection);
        bindings.WriteActiveCountText();
        bindings.RequestCanvasRepaint();
        bindings.MarkDirty();
        bindings.RefreshGlobalHotkeys();
    }
}
