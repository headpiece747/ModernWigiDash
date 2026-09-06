namespace ModernWigiDash.App.LibreHardwareService;

/// <summary>
/// One parsed LHS map header: the metadata size, last-update stamp, and the
/// index/data descriptor block offsets and lengths. The wire-format fact
/// (where the data block starts) is encoded exactly once; the map source sizes
/// the copy from it and the reader bounds-checks the parse against it. A
/// header-layout change moves one file, not two.
/// </summary>
internal sealed record LhsHeader(
    int MetaDataSize,
    long LastUpdate,
    int Msb,
    int IndexLength,
    int IndexOffset,
    int IndexFormat,
    int DataLength,
    int DataOffset);
