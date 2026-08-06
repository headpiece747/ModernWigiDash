using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

internal sealed class TwitchTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ModernWigiDash.TwitchToken.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "twitch-auth.bin");

    public TwitchTokenSet? Load()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;

            byte[] protectedBytes = File.ReadAllBytes(TokenPath);
            byte[] plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TwitchTokenSet>(plaintext, JsonOptions);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(TwitchTokenSet tokenSet)
    {
        string? directory = Path.GetDirectoryName(TokenPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to determine the Twitch token storage directory.");

        Directory.CreateDirectory(directory);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(tokenSet, JsonOptions);
        byte[] protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        string temporaryPath = TokenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, TokenPath, overwrite: true);
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
            if (File.Exists(TokenPath)) File.Delete(TokenPath);
        }
        catch (IOException)
        {
            // Logout should remain best-effort if the local file is temporarily unavailable.
            System.Diagnostics.Debug.WriteLine("Twitch token file delete failed; logout continues best-effort.");
        }
    }
}
