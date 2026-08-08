using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets.Twitch;

internal sealed class TwitchTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ModernWigiDash.TwitchToken.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string DefaultTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "twitch-auth.bin");
    private readonly string _tokenPath;

    public TwitchTokenStore() : this(DefaultTokenPath) { }

    /// <summary>Test seam: point the store at an isolated file path.</summary>
    internal TwitchTokenStore(string tokenPath) => _tokenPath = tokenPath;

    public TwitchTokenSet? Load()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return null;

            byte[] protectedBytes = File.ReadAllBytes(_tokenPath);
            byte[] plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TwitchTokenSet>(plaintext, JsonOptions);
        }
        catch (CryptographicException ex)
        {
            // Entropy changed or the file was written by another user/scope —
            // the stored session is unrecoverable, so a fresh login is required.
            System.Diagnostics.Debug.WriteLine($"Twitch token unprotect failed (session reset): {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Twitch token file corrupted (session reset): {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Twitch token read failed: {ex.Message}");
            return null;
        }
    }

    public void Save(TwitchTokenSet tokenSet)
    {
        string? directory = Path.GetDirectoryName(_tokenPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to determine the Twitch token storage directory.");

        Directory.CreateDirectory(directory);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(tokenSet, JsonOptions);
        byte[] protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        string temporaryPath = _tokenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _tokenPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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
            System.Diagnostics.Debug.WriteLine("Twitch token file delete failed; logout continues best-effort.");
        }
    }
}
