namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Pure, testable parsing of PresentMon query result blobs. A dynamic query
/// blob is chain-major: one result per swap chain, each chain laid out with the
/// elements' values at their registered <c>DataOffset</c>, and every chain the
/// same size (<see cref="ChainStrideBytes"/>). A frame-query blob is a single
/// frame's worth of per-element values read at each element's <c>DataOffset</c>.
/// Numeric poll results are stored as IEEE-754 doubles.
/// </summary>
internal static class PresentMonBlobReader
{
    /// <summary>
    /// Total bytes occupied by one swap chain's worth of dynamic-query results:
    /// the maximum <c>DataOffset + DataSize</c> across all elements so any
    /// alignment padding between fields is included when stepping between chains.
    /// </summary>
    public static int ChainStrideBytes(IReadOnlyList<PresentMonQueryElement> elements)
    {
        int stride = 0;
        foreach (var element in elements)
        {
            int end = checked((int)element.DataOffset + (int)element.DataSize);
            if (end > stride)
                stride = end;
        }

        return stride;
    }

    /// <summary>Reads one IEEE-754 double at <paramref name="offset"/>.</summary>
    public static double ReadDouble(ReadOnlySpan<byte> blob, long offset)
        => BitConverter.ToDouble(blob[checked((int)offset)..]);

    /// <summary>
    /// Reads an element's double from a dynamic-query blob for a given swap
    /// chain, stepping past the preceding chains by the chain stride.
    /// </summary>
    public static double ReadDynamicDouble(
        ReadOnlySpan<byte> blob,
        int chainIndex,
        int chainStrideBytes,
        PresentMonQueryElement element)
        => ReadDouble(blob, checked((long)chainIndex * chainStrideBytes + (long)element.DataOffset));

    /// <summary>
    /// Reads an element's value from a dynamic-query blob, matching the
    /// service's storage width: numeric metrics are 8-byte IEEE-754 doubles,
    /// enum-typed metrics (PRESENT_MODE) are 4-byte int32 — the element's
    /// DataSize reports the actual width. Reading an enum as a double would
    /// swallow the next element's bytes and yield garbage.
    /// </summary>
    public static double ReadDynamicElement(
        ReadOnlySpan<byte> blob,
        int chainIndex,
        int chainStrideBytes,
        PresentMonQueryElement element)
    {
        int offset = checked((int)((long)chainIndex * chainStrideBytes + (long)element.DataOffset));
        return element.DataSize switch
        {
            4 => BitConverter.ToInt32(blob[offset..]),
            8 => ReadDouble(blob, offset),
            _ => throw new NotSupportedException($"Unsupported PresentMon dynamic element DataSize: {element.DataSize}."),
        };
    }

    /// <summary>
    /// Reads an element's double from a single frame-query blob (one frame's
    /// worth of data) at the element's registered offset.
    /// </summary>
    public static double ReadFrameDouble(
        ReadOnlySpan<byte> frameBlob,
        PresentMonQueryElement element)
        => ReadDouble(frameBlob, (long)element.DataOffset);
}
