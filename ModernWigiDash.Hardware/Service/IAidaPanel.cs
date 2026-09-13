namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The AIDA64 panel image-frame read facet of the vendor WigiDash service
/// (ADR-0022). Stateless reads over the established channel; the client owns
/// the connection.
/// </summary>
public interface IAidaPanel
{
    /// <summary>True when the AIDA provider is initialized and ready to serve frames.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Reads a raw byte range from the AIDA64 mmap. Returns null when the read
    /// fails or the provider is not ready (the tolerant-parsing rule: no throw
    /// into the render tick).
    /// </summary>
    byte[]? ReadAidaMmap(int offset, int length);
}
