namespace ModernWigiDash.App;

/// <summary>The pure description of one page tab — the facts
/// <see cref="PageTabsView.Rebuild"/> derives and renders, testable without WPF:
/// which tab is active, whether deletion is allowed (never on the last page),
/// and the tab label.</summary>
internal sealed record PageTabItem(string PageName, int Index, bool IsActive, bool CanDelete);
