using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// One stored CalDAV credential: the app-specific password for a feed's
/// account. Keyed by the feed id (the stable slug the profile persists), so a
/// traveling profile re-resolves its password against the same key on another
/// machine instead of smuggling the secret across. The username is NOT stored
/// here (it rides the feed identity); only the secret does.
/// </summary>
internal sealed record CalendarCredential(string FeedId, string Password)
{
    /// <summary>The password for a feed, or null when none is stored.</summary>
    public string? PasswordFor(string feedId)
        => string.Equals(FeedId, feedId, StringComparison.Ordinal) ? Password : null;
}

/// <summary>
/// The machine-local CalDAV credential store (the TwitchTokenStore precedent):
/// DPAPI-protected (CurrentUser scope + app entropy), one file holding every
/// stored credential, an atomic temp-file save, and best-effort ACL hardening
/// to the current user. Secrets never ride the profile (which travels between
/// machines) -- they live here, resolved at fetch time and injected into the
/// fetcher per call. A corrupt or absent file degrades to "no credentials" with
/// one log line (the absent-service house pattern), never a throw: a missing
/// password means the producer prompts again, not that the app crashes.
/// </summary>
internal sealed class CalendarCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ModernWigiDash.CalendarCredential.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "calendar-credentials.bin");

    private readonly string _path;

    public CalendarCredentialStore() : this(DefaultPath) { }

    /// <summary>Test seam: point the store at an isolated file path.</summary>
    internal CalendarCredentialStore(string path) => _path = path;

    /// <summary>
    /// Loads the stored password for a feed id, or null when none is stored (or
    /// the file is absent/corrupt). A corrupt file logs one line and returns
    /// null rather than throwing: the producer treats a missing password as
    /// "prompt again", which is the correct recovery.
    /// </summary>
    public string? LoadPassword(string feedId)
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            byte[] protectedBytes = File.ReadAllBytes(_path);
            byte[] plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            List<CalendarCredential>? all = JsonSerializer.Deserialize<List<CalendarCredential>>(plaintext, JsonOptions);
            return all?.FirstOrDefault(c => c.PasswordFor(feedId) is not null)?.Password;
        }
        catch (CryptographicException ex)
        {
            FileLog.Write($"[CAL-STORE] Credential unprotect failed (treated as absent): {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CAL-STORE] Credential file corrupted (treated as absent): {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            FileLog.Write($"[CAL-STORE] Credential read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stores (or replaces) the password for a feed id. Atomic: writes a temp
    /// file then moves it over the target, so a crash mid-write never leaves a
    /// truncated credential file. Creates the directory when missing.
    /// </summary>
    public void SavePassword(string feedId, string password)
    {
        List<CalendarCredential> all = LoadAll();
        all.RemoveAll(c => string.Equals(c.FeedId, feedId, StringComparison.Ordinal));
        all.Add(new CalendarCredential(feedId, password));
        WriteAll(all);
    }

    /// <summary>Removes the stored password for a feed id (feed deleted).</summary>
    public void DeletePassword(string feedId)
    {
        List<CalendarCredential> all = LoadAll();
        all.RemoveAll(c => string.Equals(c.FeedId, feedId, StringComparison.Ordinal));
        WriteAll(all);
    }

    private List<CalendarCredential> LoadAll()
    {
        try
        {
            if (!File.Exists(_path))
                return [];

            byte[] protectedBytes = File.ReadAllBytes(_path);
            byte[] plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<CalendarCredential>>(plaintext, JsonOptions) ?? [];
        }
        catch (CryptographicException ex)
        {
            FileLog.Write($"[CAL-STORE] Credential unprotect failed (treated as absent): {ex.Message}");
            return [];
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CAL-STORE] Credential file corrupted (treated as absent): {ex.Message}");
            return [];
        }
        catch (IOException ex)
        {
            FileLog.Write($"[CAL-STORE] Credential read failed: {ex.Message}");
            return [];
        }
    }

    private void WriteAll(List<CalendarCredential> all)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to determine the calendar credential storage directory.");

        Directory.CreateDirectory(directory);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(all, JsonOptions);
        byte[] protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, overwrite: true);
            RestrictToCurrentUser(_path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Hardens the credential file ACL to the current user only. DPAPI
    /// CurrentUser already prevents other users from unprotecting the
    /// ciphertext, but a restrictive ACL also hides the ciphertext from
    /// same-user snooping tools. Best-effort: ACL failures must not break a
    /// save.
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            using var currentIdentity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? user = currentIdentity?.User;
            if (user is null)
                return;

            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Calendar credential ACL hardening failed (best-effort): {ex.Message}");
        }
    }
}
