namespace Spyware.Collectors.Notes;

/// <summary>
/// Sticky Notes guarda las fechas como FILETIME: ticks de 100 ns desde 1601-01-01 UTC.
/// Es el mismo origen que <c>DateTime.Ticks</c> en .NET, así que la conversión es directa
/// (a diferencia de Chrome, que usa microsegundos y hay que multiplicar por 10).
/// </summary>
public static class NoteTime
{
    public static DateTime? FromTicks(long? filetime)
    {
        if (filetime is null or <= 0) return null;

        long value = filetime.Value;
        if (value > DateTime.MaxValue.Ticks) return null;

        DateTime converted = new(value, DateTimeKind.Utc);

        // Un valor absurdo (año 9.000 o similar) viene de una columna reutilizada: mejor no inventar.
        return converted.Year is >= 1990 and <= 2100 ? converted : null;
    }

    /// <summary>SQLite puede devolver el número como long, double o texto según la columna.</summary>
    public static DateTime? FromValue(object? value) => value switch
    {
        null or DBNull => null,
        long number => FromTicks(number),
        int number => FromTicks(number),
        double number => FromTicks((long)number),
        string text => FromText(text),
        _ => null
    };

    private static DateTime? FromText(string text)
    {
        string trimmed = text.Trim();

        if (long.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out long ticks))
            return FromTicks(ticks);

        return DateTime.TryParse(
            trimmed, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
                ? parsed
                : null;
    }
}
