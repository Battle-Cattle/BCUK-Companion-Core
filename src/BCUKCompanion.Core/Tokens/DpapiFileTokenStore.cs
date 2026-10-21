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
    private readonly string _tempPath;

    /// <summary>Creates a store that keeps the encrypted token at <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The token file's path.</param>
    public DpapiFileTokenStore(string filePath)
    {
        _filePath = filePath;
        _tempPath = filePath + ".tmp";
    }

    /// <summary>Reads and decrypts the saved token.</summary>
    /// <returns>The token, or null if none is saved or it can't be decrypted (e.g. it was saved by another Windows user).</returns>
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

    /// <summary>Encrypts <paramref name="token"/> for the current Windows user and replaces the saved token file with it.</summary>
    /// <param name="token">The companion bearer token to save.</param>
    public void Save(string token)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(token);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            // Written to a temp file, flushed to disk, then swapped in, so a crash or power loss
            // mid-write can't leave a truncated blob that silently logs the user out.
            try
            {
                using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encrypted, 0, encrypted.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(_tempPath, _filePath, overwrite: true);
            }
            catch
            {
                // Don't leave a copy of the token behind; the saved token file is untouched.
                TryDelete(_tempPath);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Deletes the saved token file, and any temp copy an interrupted <see cref="Save"/> left behind.</summary>
    public void Clear()
    {
        // No File.Exists checks: it reports false when it can't check (e.g. access denied), which
        // would silently leave a token behind. File.Delete is a no-op for a missing file, but
        // throws if the folder itself is missing (nothing was ever saved).
        DeleteIfFolderExists(_filePath);
        DeleteIfFolderExists(_tempPath);
    }

    /// <summary>Deletes <paramref name="path"/>; a missing file or folder is not an error.</summary>
    /// <param name="path">The full path of the file to delete.</param>
    private static void DeleteIfFolderExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Best-effort delete used while already handling a failure, so it never masks that failure.</summary>
    /// <param name="path">The full path of the file to delete; any I/O or permission error is ignored.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
