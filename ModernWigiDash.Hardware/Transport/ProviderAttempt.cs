namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// One connect provider's acquisition result: the backend it produced, or
/// WHICH failure stopped it. The two travel together on purpose — a provider
/// that failed because the display is BUSY and one that found nothing must not
/// collapse into the same "no device" verdict on the way out.
///
/// A top-level type, not a member of the transport, because BOTH adapters build
/// one: each driver stack owns its own acquisition and therefore its own error
/// vocabulary. When the transport's LibUsbDotNet error table sat in the connect
/// loop it could not work — the adapter swallowed its exception and returned a
/// bare null, so the mapping was dead code whose comment claimed otherwise
/// (2026-09-27). The reason has to be produced where the error code lives.
/// </summary>
internal readonly record struct ProviderAttempt(ITransferBackend? Backend, ConnectionState Failure)
{
    /// <summary>The attempt opened the device.</summary>
    public static ProviderAttempt Opened(ITransferBackend backend) => new(backend, ConnectionState.Connected);

    /// <summary>The attempt did not open it, and says why.</summary>
    public static ProviderAttempt Failed(ConnectionState failure) => new(null, failure);
}
