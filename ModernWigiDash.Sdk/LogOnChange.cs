namespace ModernWigiDash.Sdk;

/// <summary>
/// The log-on-change dedup rule: fires when the message differs from the
/// previous one. The sibling of <see cref="LogCadence"/> — cadence bounds
/// repetition frequency, this suppresses identical repeats until the message
/// actually changes. Single-threaded by contract (each tick body owns one
/// instance); the field compare is not synchronized.
/// </summary>
public sealed class LogOnChange
{
    private string? _last;

    /// <summary>True when <paramref name="message"/> differs from the previous
    /// call — including a first call with a non-null message (a first call
    /// with null is indistinguishable from "no change" and returns false, which
    /// is what the log-on-change callers need: silence until a real message
    /// arrives).</summary>
    public bool Changed(string? message)
    {
        if (message == _last) return false;
        _last = message;
        return true;
    }
}
