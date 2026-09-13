using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets.Spotify;

/// <summary>
/// The Spotify credential store: DPAPI-protected, atomic-save, ACL-hardened,
/// mirroring the Twitch token store. The secret lives in %LOCALAPPDATA% under a
/// per-user file, never in the profile (which travels between machines), so a
/// traveling profile re-authenticates on another machine instead of smuggling
/// the token across.
/// </summary>
internal sealed class SpotifyTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ModernWigiDash.SpotifyToken.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string DefaultTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "spotify-auth.bin");
    private readonly string _tokenPath;

    public SpotifyTokenStore() : this(DefaultTokenPath) { }

    /// <summary>Test seam: point the store at an isolated file path.</summary>
    internal SpotifyTokenStore(string tokenPath) => _tokenPath = tokenPath;

    public SpotifyTokenSet? Load()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return null;

            byte[] protectedBytes = File.ReadAllBytes(_tokenPath);
            byte[] plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SpotifyTokenSet>(plaintext, JsonOptions);
        }
        catch (CryptographicException ex)
        {
            // Entropy changed or the file was written by another user/scope —
            // the stored session is unrecoverable, so a fresh login is required.
            FileLog.Write($"[SPOTIFY-TOKEN] Token unprotect failed (session reset): {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SPOTIFY-TOKEN] Token file corrupted (session reset): {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            FileLog.Write($"[SPOTIFY-TOKEN] Token read failed: {ex.Message}");
            return null;
        }
    }

    public void Save(SpotifyTokenSet tokenSet)
    {
        string? directory = Path.GetDirectoryName(_tokenPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to determine the Spotify token storage directory.");

        Directory.CreateDirectory(directory);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(tokenSet, JsonOptions);
        byte[] protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        string temporaryPath = _tokenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _tokenPath, overwrite: true);
            RestrictToCurrentUser(_tokenPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Hardens the token file ACL to the current user only. DPAPI CurrentUser
    /// already prevents other users from unprotecting the ciphertext, but a
    /// restrictive ACL also hides the ciphertext from same-user snooping tools.
    /// Best-effort: ACL failures must not break login.
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            using var currentIdentity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? user = currentIdentity?.User;
            if (user is null)
            {
                // No resolvable user SID — ACL hardening can't proceed, but the
                // DPAPI-protected ciphertext is still safe. Skip best-effort.
                return;
            }

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
            System.Diagnostics.Debug.WriteLine($"Spotify token ACL hardening failed (best-effort): {ex.Message}");
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
        }
        catch (IOException)
        {
            // Logout should remain best-effort if the local file is temporarily unavailable.
            System.Diagnostics.Debug.WriteLine("Spotify token file delete failed; logout continues best-effort.");
        }
    }
}
