using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BCUKCompanion.Core.Tokens;

/// <summary>
/// Stores the companion token on disk, encrypted with Windows DPAPI
/// (CurrentUser scope) so the plaintext token never touches disk and can
/// only be decrypted by the same Windows user account that saved it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiFileTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BCUKCompanion.Token.v1");

    private readonly string _filePath;

    public DpapiFileTokenStore(string filePath)
    {
        _filePath = filePath;
    }

    public string? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            byte[] encrypted = File.ReadAllBytes(_filePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // Blob is corrupted or was written by a different user/machine — treat as logged out.
            return null;
        }
    }

    public void Save(string token)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(token);
        byte[] encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, encrypted);
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
