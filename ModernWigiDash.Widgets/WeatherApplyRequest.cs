namespace ModernWigiDash.Widgets;

/// <summary>
/// The one value the fetch flow hands the host seam for a single apply: the
/// snapshot, the expected data-version guard (null = no version check — the
/// fetch path; the boot load passes the version captured before its await),
/// the identity guard (the captured key's live re-check), and the
/// resolved-identity copies. The optional members follow the apply policy's
/// null-keeps semantics at the host — one shape for both call sites, so the
/// fetch and boot paths differ by field, not by function signature.
/// </summary>
internal sealed record WeatherApplyRequest(
    WeatherSnapshot Snapshot,
    int? ExpectedVersion,
    Func<bool>? IdentityGuard,
    IReadOnlyList<GeocodeCandidate>? Candidates,
    double? Population,
    string? ResolvedName);