using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The chunked bulk-write policy behind the LibUsb backend: payloads are
/// always written in bounded chunks sized for the legacy libusb driver's
/// throughput (it stalls on multi-megabyte single transfers), advancing by
/// the actually-transferred length so a short write never skips a gap. Pure
/// policy over a write delegate — testable without a device.
/// </summary>
internal static class ChunkedBulkWrite
{
    /// <summary>Maximum bytes per bulk-write chunk (the driver's reliable size).</summary>
    public const int ChunkSize = 262144;

    /// <summary>Per-chunk timeout, ms (the driver's partial-data stall bound).</summary>
    public const int ChunkTimeoutMs = 10000;

    /// <summary>
    /// Writes <paramref name="data"/> in bounded chunks. Each chunk starts at
    /// the accumulated transferred length — not a nominal stride — so a short
    /// write continues from where the device actually accepted data. Returns
    /// true only when every byte was written.
    /// </summary>
    public static bool Write(
        byte[] data,
        Func<int, int, (bool Ok, int Transferred, string ErrorDetail)> writeChunk,
        out int transferred,
        Action<string>? log = null)
    {
        transferred = 0;
        int totalBytes = data.Length;
        int numChunks = (totalBytes + ChunkSize - 1) / ChunkSize;

        for (int i = 0; i < numChunks; i++)
        {
            int offset = transferred;
            int size = Math.Min(ChunkSize, totalBytes - offset);

            var (ok, transferLength, errorDetail) = writeChunk(offset, size);
            if (!ok || transferLength <= 0)
            {
                log?.Invoke($"Chunk {i}/{numChunks} failed: error={errorDetail} transferred={transferLength}");
                return false;
            }

            transferred += transferLength;
        }

        return transferred == totalBytes;
    }
}
