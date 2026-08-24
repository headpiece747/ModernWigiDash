namespace ModernWigiDash.Core.Models;

/// <summary>
/// The named verdicts of the profile-file import (the single funnel
/// <see cref="ProfileOps.ImportProfileFile"/> is the only producer): a
/// loaded, rehydrated profile; an absent file; a file rejected by the
/// sanitizer's size guard BEFORE any read (the DoS boundary: oversized
/// untrusted junk must never become a string in the first place); or a
/// file that read but could not be parsed. Callers map the verdicts to
/// their own surface (the window's dialogs, the boot load's log) instead
/// of re-spelling the read-and-reject sequence, so the reject rule is
/// enforced at exactly one site no matter how many import entries the app
/// grows. Pattern match on the nested cases.
/// </summary>
public abstract record ProfileImportOutcome
{
    private ProfileImportOutcome()
    {
    }

    /// <summary>The file loaded, sanitized (when untrusted), and rehydrated.</summary>
    public sealed record Loaded(ProfileLayout Profile) : ProfileImportOutcome;

    /// <summary>No file at the path (the first-boot case).</summary>
    public sealed record Absent : ProfileImportOutcome;

    /// <summary>
    /// The file exceeds <see cref="ProfileImportSanitizer.MaxImportFileBytes"/>;
    /// rejected before any read. <see cref="FileBytes"/> carries the measured
    /// size for the caller's message.
    /// </summary>
    public sealed record TooLarge(long FileBytes) : ProfileImportOutcome;

    /// <summary>
    /// The file read (or the read itself failed) but no profile could be
    /// parsed. <see cref="Detail"/> is the caller-facing reason.
    /// </summary>
    public sealed record Failed(string Detail) : ProfileImportOutcome;
}
