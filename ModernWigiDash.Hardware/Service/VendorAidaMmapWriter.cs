using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The AIDA64 panel master's write half over the vendor WCF service: the
/// service owns the shared map (with the vendor's mutex), and its request quota
/// caps writes at ~64 KB, so only the small handshake goes through here. The
/// client is resolved from <see cref="VendorService"/> per call, so the app's
/// one wired client is shared and no second WCF connection is opened.
/// </summary>
internal sealed class VendorAidaMmapWriter : IAidaMmapWriter
{
    /// <inheritdoc />
    public bool TryInit(out string? error)
    {
        var client = VendorService.Instance;
        if (client is null)
        {
            error = "vendor service client not wired";
            return false;
        }

        if (client.TryInitAidaProvider())
        {
            error = null;
            return true;
        }

        error = "AIDA64 provider unavailable";
        return false;
    }

    /// <inheritdoc />
    public bool TryWrite(int offset, byte[] data, out string? error)
    {
        var client = VendorService.Instance;
        if (client is null)
        {
            error = "vendor service client not wired";
            return false;
        }

        if (client.TryWriteAidaMmap(offset, data))
        {
            error = null;
            return true;
        }

        error = "AIDA64 map write rejected";
        return false;
    }
}
