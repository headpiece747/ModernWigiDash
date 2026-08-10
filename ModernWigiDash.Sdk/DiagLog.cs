namespace ModernWigiDash.Sdk;

/// <summary>
/// A diagnostic log line with a cadence rule: composes <see cref="LogCadence"/>
/// (first-log / every-Nth) with <see cref="FileLog"/> and bakes the caller's
/// category tag into every line — the tag is declared once at construction
/// instead of being hand-baked into each message string, so a category can
/// never drift between its call sites.
/// </summary>
public sealed class DiagLog(string category, int cadence, bool logFirst = false, Action<string>? write = null)
{
    private readonly LogCadence _cadence = new(cadence, logFirst);
    private readonly Action<string> _write = write ?? FileLog.Write;

    /// <summary>
    /// Writes the message when the cadence is due, tagged with the category —
    /// callers pass only the message body, never the "[...]" prefix. The
    /// optional <paramref name="write"/> seam (tests only) mirrors
    /// FrameDelivery's injected log callback; production defaults to
    /// <see cref="FileLog"/>.
    /// </summary>
    public void Write(string message)
    {
        if (_cadence.Due())
            _write($"[{category}] {message}");
    }
}
