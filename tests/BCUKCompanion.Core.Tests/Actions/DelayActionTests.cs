using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public class DelayActionTests
{
    private static readonly IEventActionContext Context = new NoServiceEventActionContext();

    [Theory]
    [InlineData(1)]
    [InlineData(1800)]
    [InlineData(3600)]
    public void Validate_WithinBounds_ReturnsNoErrors(int delaySeconds)
    {
        var action = new DelayAction { DelaySeconds = delaySeconds };
        Assert.Empty(action.Validate(Context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    [InlineData(-5)]
    public void Validate_OutOfBounds_ReturnsError(int delaySeconds)
    {
        var action = new DelayAction { DelaySeconds = delaySeconds };
        Assert.NotEmpty(action.Validate(Context));
    }

    [Fact]
    public async Task ExecuteAsync_DelaysApproximatelyRequestedDuration()
    {
        var action = new DelayAction { DelaySeconds = 1 };
        var start = DateTime.UtcNow;

        var success = await action.ExecuteAsync(Context, CancellationToken.None);

        Assert.True(success);
        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellation()
    {
        var action = new DelayAction { DelaySeconds = 60 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.ExecuteAsync(Context, cts.Token));
    }
}
