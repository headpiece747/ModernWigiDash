namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// PresentMon status codes (PresentMonAPI.h — success is zero, first in
/// enum; the rest are positional, NOT named-assigned).
/// </summary>
internal enum PmStatus
{
    Success = 0,
    Failure = 1,
    BadArgument = 2,
    BadHandle = 3,
    ServiceError = 4,
    InvalidEtlFile = 5,
    InvalidPid = 6,
    AlreadyTrackingProcess = 7,
    UnableToCreateNsm = 8,
    InvalidAdapterId = 9,
    OutOfRange = 10,
    InsufficientBuffer = 11,
    PipeError = 12,
    SessionNotOpen = 13,
    MiddlewareMissingPath = 14,
    NonexistentFilePath = 15,
    MiddlewareInvalidSignature = 16,
    MiddlewareMissingEndpoint = 17,
    MiddlewareVersionLow = 18,
    MiddlewareVersionHigh = 19,
    MiddlewareServiceMismatch = 20,
    QueryMalformed = 21,
    ModeMismatch = 22,
    FeatureDisabled = 23,
}
