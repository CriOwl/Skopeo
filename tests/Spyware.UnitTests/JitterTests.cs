using Spyware.Core.Scheduling;
using Xunit;

namespace Spyware.UnitTests;

public class JitterTests
{
    [Theory]
    [InlineData(60, 10)]
    [InlineData(900, 60)]
    public void Jitter_StaysInsideBounds(int baseSeconds, int jitterSeconds)
    {
        var baseInterval = TimeSpan.FromSeconds(baseSeconds);
        var random = new Random(1234);

        for (int i = 0; i < 500; i++)
        {
            TimeSpan result = Jitter.Apply(baseInterval, jitterSeconds, random);

            Assert.InRange(result.TotalSeconds, baseSeconds - jitterSeconds, baseSeconds + jitterSeconds);
            Assert.True(result >= TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Jitter_WithZeroJitter_ReturnsBase() =>
        Assert.Equal(TimeSpan.FromSeconds(60), Jitter.Apply(TimeSpan.FromSeconds(60), 0));

    [Fact]
    public void Jitter_Percent_AppliesTwentyPercent()
    {
        TimeSpan result = Jitter.ApplyPercent(TimeSpan.FromMinutes(1), 0.20, new Random(7));

        Assert.InRange(result.TotalSeconds, 48, 72);
    }
}
