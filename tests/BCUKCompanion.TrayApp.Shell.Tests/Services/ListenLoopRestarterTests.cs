using BCUKCompanion.TrayApp.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

public sealed class ListenLoopRestarterTests
{
    private readonly FakeTimeProvider clock = new();

    [Fact]
    public void AFirstFault_RestartsAtOnce()
    {
        var restarter = new ListenLoopRestarter(clock);
        int restarts = 0;

        Task task = restarter.RestartAfterFaultAsync(() => restarts++, ex => throw ex);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void FaultsInARow_WaitLongerEachTime()
    {
        var restarter = new ListenLoopRestarter(clock);

        Assert.Equal(TimeSpan.Zero, restarter.NextDelay());
        Assert.InRange(restarter.NextDelay(), TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2));
        Assert.InRange(restarter.NextDelay(), TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(2.4));
        Assert.InRange(restarter.NextDelay(), TimeSpan.FromSeconds(3.2), TimeSpan.FromSeconds(4.8));
    }

    [Fact]
    public void AFaultAfterAQuietPeriod_CountsAsTheFirstAgain()
    {
        var restarter = new ListenLoopRestarter(clock);
        restarter.NextDelay();
        restarter.NextDelay();

        clock.Advance(ListenLoopRestarter.QuietPeriod);

        Assert.Equal(TimeSpan.Zero, restarter.NextDelay());
    }

    [Fact]
    public async Task ARepeatedFault_RestartsOnlyOnceItsDelayHasPassed()
    {
        var restarter = new ListenLoopRestarter(clock);
        int restarts = 0;
        await restarter.RestartAfterFaultAsync(() => restarts++, ex => throw ex);

        Task second = restarter.RestartAfterFaultAsync(() => restarts++, ex => throw ex);
        clock.Advance(TimeSpan.FromSeconds(0.7));
        Assert.False(second.IsCompleted);
        Assert.Equal(1, restarts);

        clock.Advance(TimeSpan.FromSeconds(0.6));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, restarts);
    }

    [Fact]
    public void ARestartThatThrows_IsReportedRatherThanFaultingTheTask()
    {
        var restarter = new ListenLoopRestarter(clock);
        Exception? reported = null;

        Task task = restarter.RestartAfterFaultAsync(() => throw new InvalidOperationException("no"), ex => reported = ex);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal("no", reported?.Message);
    }
}
