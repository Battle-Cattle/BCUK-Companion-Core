using BCUKCompanion.Core.Events;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class ReconnectBackoffTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void GrowsExponentially(int attempt, double expectedSeconds)
    {
        TimeSpan delay = ReconnectBackoff.GetDelay(attempt);
        Assert.Equal(expectedSeconds, delay.TotalSeconds);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(100)]
    public void CapsAtThirtySeconds(int attempt)
    {
        TimeSpan delay = ReconnectBackoff.GetDelay(attempt);
        Assert.Equal(30, delay.TotalSeconds);
    }

    [Fact]
    public void NegativeAttemptTreatedAsZero()
    {
        Assert.Equal(ReconnectBackoff.GetDelay(0), ReconnectBackoff.GetDelay(-5));
    }
}
