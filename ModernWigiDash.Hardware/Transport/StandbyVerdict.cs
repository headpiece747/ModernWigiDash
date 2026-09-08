namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The standby verdict's one spelling: the log line a non-confirmed or failed
/// standby must carry. The session-end path (<see cref="DisplayDeviceEngine.TryGoToStandby"/>)
/// and the dispose path (<see cref="DisplayDeviceEngine.Dispose"/>) both route
/// through it, so the NOT-confirmed wording (load-bearing since the 2026-08-21
/// on-device incident) cannot drift between the two entry points. The engine's
/// bounded-wait routine owns the completion-versus-result rule; this module
/// owns only the verdict's vocabulary.
/// </summary>
public static class StandbyVerdict
{
    /// <summary>The failure line for a standby that threw (the vendor exception's own wording).</summary>
    public static string FailureLine(Exception error, bool duringDispose)
        => duringDispose
            ? $"Standby failed during dispose: {error.Message}"
            : $"Standby failed: {error.Message}";

    /// <summary>The NOT-confirmed line for a standby that did not confirm: the
    /// settled-but-failed writes (the display may stay lit) and the abandoned
    /// hang (the bounded wait expired). Null when there is nothing to report
    /// (a confirmed standby, or no device at all).</summary>
    public static string? NotConfirmedLine(bool settled, bool confirmed, bool hasTransport, bool duringDispose)
    {
        if (confirmed || !hasTransport) return null;
        string prefix = duringDispose ? "Standby NOT confirmed during dispose" : "Standby NOT confirmed";
        if (!settled)
        {
            string abandoned = duringDispose
                ? "the bounded close wait expired — a hung standby was abandoned at exit, and the display may stay lit"
                : "the bounded wait expired — a hung standby was abandoned, and the display may stay lit";
            return $"{prefix}: {abandoned}";
        }
        return $"{prefix}: the standby control writes did not succeed — the display may stay lit";
    }
}
