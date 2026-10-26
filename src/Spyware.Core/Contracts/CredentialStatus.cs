namespace Spyware.Core.Contracts;

/// <summary>
/// Estados de extracción. Separan explícitamente "pude leer" de "pude descifrar".
/// </summary>
public static class CredentialStatus
{
    public const string Decrypted = "decrypted";
    public const string EncryptedOnly = "encrypted_only";
    public const string AccessDenied = "access_denied";
    public const string Locked = "locked";
    public const string Unsupported = "unsupported";
    public const string Corrupted = "corrupted";
    public const string NotFound = "not_found";
}
