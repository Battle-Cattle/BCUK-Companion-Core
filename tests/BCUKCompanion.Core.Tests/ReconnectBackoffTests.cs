using BCUKCompanion.Core.Events;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class ReconnectBackoffTests
{
    private const double JitterFraction = 0.2;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void GrowsExponentiallyWithinJitterBounds(int attempt, double baseSeconds)
    {
        var random = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            TimeSpan delay = ReconnectBackoff.GetDelay(attempt, random);
            Assert.InRange(delay.TotalSeconds, baseSeconds * (1 - JitterFraction), baseSeconds * (1 + JitterFraction));
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(100)]
    public void CapsAtThirtySeconds(int attempt)
    {
        var random = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            TimeSpan delay = ReconnectBackoff.GetDelay(attempt, random);
            Assert.True(delay.TotalSeconds <= 30, $"Expected delay <= 30s, got {delay.TotalSeconds}s");
        }
    }

    [Fact]
    public void NegativeAttemptTreatedAsZero()
    {
        var random = new Random(1);
        TimeSpan zeroDelay = ReconnectBackoff.GetDelay(0, random);

        var sameSeedRandom = new Random(1);
        TimeSpan negativeDelay = ReconnectBackoff.GetDelay(-5, sameSeedRandom);

        Assert.Equal(zeroDelay, negativeDelay);
    }

    [Fact]
    public void JitterVariesDelayAcrossCalls()
    {
        var random = new Random(123);
        var delays = new HashSet<double>();

        for (int i = 0; i < 20; i++)
        {
            delays.Add(ReconnectBackoff.GetDelay(3, random).TotalSeconds);
        }

        Assert.True(delays.Count > 1, "Expected jitter to produce varying delays across calls.");
    }

    [Fact]
    public void DelayWithoutExplicitRandomStillWithinBounds()
    {
        // Exercises the default Random.Shared path.
        TimeSpan delay = ReconnectBackoff.GetDelay(2);
        Assert.InRange(delay.TotalSeconds, 4 * (1 - JitterFraction), 4 * (1 + JitterFraction));
    }
}
