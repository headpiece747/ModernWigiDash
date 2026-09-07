namespace ModernWigiDash.Sdk;

/// <summary>
/// The credential facet of the widget host context: the member a widget needs
/// to store a machine-local secret (the calendar feed editor's CalDAV password).
/// Split from <see cref="IModernWigiDashContext"/> so a widget that stores
/// credentials depends on this capability explicitly instead of seeing every
/// host service (the <c>IWidgetActionInvoker</c> / <c>IWidgetEditorProvider</c>
/// optional-facet precedent). A host without a credential store simply does not
/// implement it; <see cref="ModernWidgetBase.CredentialContext"/> is then null
/// and the store degrades to a no-op.
/// </summary>
public interface IWidgetCredentialContext
{
    /// <summary>
    /// Stores (or replaces) the machine-local CalDAV password for a calendar
    /// feed, keyed by the feed id. The secret lives in the host's DPAPI-backed
    /// credential store, never in the profile (which travels between machines),
    /// so a traveling profile re-resolves its password on another machine
    /// instead of smuggling the secret across. The App's context writes through
    /// its <c>CalendarCredentialStore</c>. Safe from any thread.
    /// </summary>
    /// <param name="feedId">The stable feed slug the password is keyed by.</param>
    /// <param name="password">The app-specific password to store.</param>
    void SaveCalendarCredential(string feedId, string password);
}
