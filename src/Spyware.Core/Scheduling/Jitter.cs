namespace Spyware.Core.Scheduling;

public static class Jitter
{
    private static readonly Random Shared = new();

    /// <summary>Desplaza el intervalo ±jitterSeconds, mínimo 1 segundo.</summary>
    public static TimeSpan Apply(TimeSpan baseInterval, int jitterSeconds, Random? random = null)
    {
        if (jitterSeconds <= 0) return baseInterval;

        Random rng = random ?? Shared;
        double offsetSeconds = (rng.NextDouble() * 2d - 1d) * jitterSeconds;
        double ms = baseInterval.TotalMilliseconds + offsetSeconds * 1000d;
        return TimeSpan.FromMilliseconds(Math.Max(1000d, ms));
    }

    public static TimeSpan ApplyPercent(TimeSpan baseInterval, double percent, Random? random = null)
    {
        if (percent <= 0d) return baseInterval;

        Random rng = random ?? Shared;
        double factor = 1d + (rng.NextDouble() * 2d - 1d) * percent;
        return TimeSpan.FromMilliseconds(Math.Max(1000d, baseInterval.TotalMilliseconds * factor));
    }

    public static int Next(int minimum, int maximum, Random? random = null)
    {
        Random rng = random ?? Shared;
        return maximum <= minimum ? minimum : rng.Next(minimum, maximum + 1);
    }
}
