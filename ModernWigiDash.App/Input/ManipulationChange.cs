namespace ModernWigiDash.App.Input;

/// <summary>
/// What an edit-mode manipulation changed, reported once per change through the
/// controller's <c>onManipulation</c> seam: the window applies one refresh
/// sequence per outcome instead of re-deriving "which out-param implies which
/// refresh" at each feed site.
/// </summary>
/// <param name="Changed">True when the manipulation moved/resized a widget or
/// applied snap-to-grid (profile dirty + inspector transform refresh + repaint).</param>
/// <param name="IconMoved">True when an icon grab changed the widget's icon
/// offsets (full inspector refresh).</param>
public readonly record struct ManipulationChange(bool Changed, bool IconMoved);
