namespace Spyware.Core.Contracts;

/// <summary>
/// Estados de lectura de notas. Mismo criterio que <see cref="CredentialStatus"/>:
/// "no pude abrir el archivo" y "no pude interpretar el contenido" son fallos distintos.
/// </summary>
public static class NoteStatus
{
    public const string Read = "read";

    /// <summary>Nota leída pero marcada como borrada en la aplicación de origen.</summary>
    public const string Deleted = "deleted";

    public const string AccessDenied = "access_denied";

    /// <summary>Archivo bloqueado por Sticky Notes incluso tras los reintentos de copia.</summary>
    public const string Locked = "locked";

    /// <summary>Formato o esquema que no sabemos interpretar (versión nueva, tabla distinta).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Archivo ilegible: SQLite corrupto o CFB sin firma válida.</summary>
    public const string Corrupted = "corrupted";

    public const string NotFound = "not_found";
}
