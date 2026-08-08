namespace ModernWigiDash.Service.Wcf;

/// <summary>
/// Singleton state for per-call WCF service instances. The display service is
/// registered scoped (PerCall): anything stored on the instance is lost between
/// calls, so rate-limit windows and ownership flags must live here — injected
/// once per process, shared by every request instance.
/// </summary>
public sealed class ServiceCallState
{
    public Lock SendRateLock { get; } = new();

    public DateTime SendWindowStart;
    public int SendWindowCount;

    public int TouchConsumerTaken;
}
