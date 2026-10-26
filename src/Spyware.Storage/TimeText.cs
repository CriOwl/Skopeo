using System.Globalization;

namespace Spyware.Storage;

internal static class TimeText
{
    public static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static DateTime Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;

        return DateTime.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
                ? parsed
                : DateTime.MinValue;
    }
}
