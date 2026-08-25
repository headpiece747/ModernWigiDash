namespace ModernWigiDash.App;

/// <summary>
/// The device-authorization window's decision model (the Twitch device flow):
/// the display facts, the trusted-open verdict (a tampered verification URL
/// is refused and logged before any shell-open seam runs), the copy-code
/// verdict, and the lifetime's slot-clear identity rule. Pure: the window
/// (<see cref="DialogHost.ShowDeviceAuthorization"/>) is a thin adapter over
/// it, and tests drive the same rules without a window.
/// </summary>
internal sealed class DeviceAuthorizationModel
{
    private const string RefusalMessage = "Refusing to open non-Twitch authorization URL";
    private const string OpenFailedMessage = "Unable to open the Twitch authorization page";
    private const string CopyFailedMessage = "Unable to copy the authorization code";

    private readonly string _serviceName;
    private readonly Uri _verificationUri;
    private readonly string _userCode;
    private readonly DateTimeOffset _expiresAt;
    private readonly Action<string, Exception?> _logError;

    public DeviceAuthorizationModel(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt, Action<string, Exception?> logError)
    {
        ArgumentNullException.ThrowIfNull(verificationUri);
        ArgumentNullException.ThrowIfNull(userCode);
        ArgumentNullException.ThrowIfNull(logError);
        _serviceName = serviceName;
        _verificationUri = verificationUri;
        _userCode = userCode;
        _expiresAt = expiresAt;
        _logError = logError;
    }

    public string Title => $"ModernWigiDash - {_serviceName} Login";

    public string Header => $"Authorize {_serviceName} in your browser";

    public string Code => _userCode;

    public string VerificationText => _verificationUri.AbsoluteUri;

    public string ExpirationText => $"This code expires at {_expiresAt.LocalDateTime:t}.";

    /// <summary>
    /// Opens the verification URL through the shell-open seam, after the
    /// trusted-URL gate (the shared <see cref="TrustedUriPolicy"/> composite:
    /// https on twitch.tv). A tampered URL is refused and logged; the seam
    /// never runs for it.
    /// </summary>
    public void OpenBrowser(Action<Uri> open)
    {
        if (!TrustedUriPolicy.IsTwitchAuthorizationUri(_verificationUri))
        {
            _logError(RefusalMessage, null);
            return;
        }

        try
        {
            open(_verificationUri);
        }
        catch (Exception ex)
        {
            _logError(OpenFailedMessage, ex);
        }
    }

    /// <summary>Copies the user code through the clipboard seam; a clipboard
    /// failure is logged, never thrown.</summary>
    public void CopyCode(Action<string> copy)
    {
        try
        {
            copy(_userCode);
        }
        catch (Exception ex)
        {
            _logError(CopyFailedMessage, ex);
        }
    }

    /// <summary>
    /// The lifetime's slot-clear rule: a closed window clears the host's
    /// window slot only when it IS the current window, so a late close of a
    /// replaced window never clears the replacement's slot.
    /// </summary>
    public static bool ClosedWindowClearsSlot(Window? current, Window closed)
        => ReferenceEquals(current, closed);
}
