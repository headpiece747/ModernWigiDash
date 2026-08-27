namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The PresentMon status codes the app interprets (PresentMonAPI.h
/// values, explicitly assigned: the enum brands only the codes the seam's
/// decision sites read - the success/failure split, the session-loss set,
/// the tracking/poll specifics, and the query-registrations' malformed
/// verdict. Every other code the DLL returns stays an opaque failure
/// through the <c>!= Success</c> comparisons; the header in the service
/// directory is the full reference table).
/// </summary>
internal enum PmStatus
{
    Success = 0,
    ServiceError = 4,
    InvalidPid = 6,
    AlreadyTrackingProcess = 7,
    InsufficientBuffer = 11,
    PipeError = 12,
    SessionNotOpen = 13,
    QueryMalformed = 21,
}
