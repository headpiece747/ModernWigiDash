namespace ModernWigiDash.App.Update;

/// <summary>
/// The update button's flow state. The icon/brush/visibility presentation
/// per state lives in the window's UpdateBadgeModel (App root) — the flow
/// owns only which state each event transitions to.
/// </summary>
internal enum UpdateState { Hidden, Available, Downloading, Ready }

/// <summary>
/// What the update-button click asks the window to do — the flow's one click
/// decision (the window executes it; it never re-derives the routing).
/// </summary>
internal enum UpdateClickAction { None, Download, Restart }

/// <summary>
/// One render unit from the flow: the button state + the tooltip text. The
/// icon, brush, and visibility derive from the state through the window's
/// UpdateBadgeModel.
/// </summary>
internal sealed record UpdateUiState(UpdateState State, string Tooltip);

/// <summary>
/// The pure update-flow state machine: the startup check, the download
/// progress/completion transitions, the failure transitions (check failure,
/// download failure, launch failure — all the window's "silent fall back to
/// Hidden" spellings), and the one spelling of every tooltip. The window
/// applies each returned <see cref="UpdateUiState"/> to the button elements
/// and owns the restart-prompt dialog and the close (the launch decision is
/// the flow's; the prompt/launch protocol stays with the window's service
/// calls). Testable without WPF or network — the window's own UpdateService
/// I/O seams stay the production I/O.
/// </summary>
internal sealed class UpdateFlow
{
    private UpdateState _state = UpdateState.Hidden;
    private UpdateInfo? _pendingUpdate;

    /// <summary>The current button state (read-only for observability).</summary>
    public UpdateState State => _state;

    /// <summary>The last update the flow knows about (available/ready states),
    /// or null — the window's launch prompt reads it.</summary>
    public UpdateInfo? PendingUpdate => _pendingUpdate;

    /// <summary>
    /// The startup check: no update → stay Hidden (null, no render); an
    /// update found → Available with the "Update v{version} available"
    /// tooltip. A null result is the expected up-to-date/offline/failed
    /// outcome — silent.
    /// </summary>
    public UpdateUiState? CheckResult(UpdateInfo? info)
    {
        if (info is null) return null;
        _pendingUpdate = info;
        return Render(UpdateState.Available, AvailableTooltip(info));
    }

    /// <summary>
    /// The download outcome: success arms the Ready state (the pending update
    /// is set), failure falls back to Hidden (the silent-fail spelling the
    /// window used to inline). The progress callback is the window's
    /// download tooltip update — routed through here so the tooltip spelling
    /// has one owner.
    /// </summary>
    public UpdateUiState DownloadComplete(UpdateInfo info, bool ok)
    {
        if (!ok) return Render(UpdateState.Hidden);
        _pendingUpdate = info;
        return Render(UpdateState.Ready, ReadyTooltip);
    }

    /// <summary>The failure transition: check, download, or launch failure —
    /// the button hides again, tooltip clears.</summary>
    public UpdateUiState Fail() => Render(UpdateState.Hidden);

    /// <summary>The one click decision: Available + a pending update →
    /// Download; Ready → Restart; anything else → None.</summary>
    public UpdateClickAction OnClick() => _state switch
    {
        UpdateState.Available when _pendingUpdate is not null => UpdateClickAction.Download,
        UpdateState.Ready => UpdateClickAction.Restart,
        _ => UpdateClickAction.None,
    };

    /// <summary>The Available-state tooltip — the single "Update v{version}
    /// available" spelling.</summary>
    public static string AvailableTooltip(UpdateInfo info) => $"Update v{info.Version} available";

    /// <summary>The per-progress download tooltip — the single
    /// "Downloading v{version}… {pct}%" spelling (the window's progress
    /// callback routes through this).</summary>
    public static string DownloadingTooltip(UpdateInfo info, double progress)
        => $"Downloading v{info.Version}… {progress * 100:F0}%";

    /// <summary>The Ready-state tooltip.</summary>
    public static string ReadyTooltip => "Restart to apply";

    /// <summary>
    /// Enters the Downloading state and returns its initial (0%) render.
    /// The window calls this when the download begins, then routes each
    /// progress tick through <see cref="DownloadingTooltip"/>.
    /// </summary>
    public UpdateUiState BeginDownload(UpdateInfo info)
        => Render(UpdateState.Downloading, DownloadingTooltip(info, 0));

    private UpdateUiState Render(UpdateState state, string? tooltip = null)
    {
        _state = state;
        return new UpdateUiState(state, tooltip ?? "");
    }
}
