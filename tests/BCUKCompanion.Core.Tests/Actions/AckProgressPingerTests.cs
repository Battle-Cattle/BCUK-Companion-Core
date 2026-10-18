using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public class AckProgressPingerTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task PingsWhileTheWorkRuns_AndReturnsItsResult()
    {
        int pings = 0;
        var release = new TaskCompletionSource<string>();

        Task<string> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => { Interlocked.Increment(ref pings); return Task.CompletedTask; },
            FastInterval);

        while (Volatile.Read(ref pings) < 3)
        {
            await Task.Delay(5);
        }

        release.SetResult("done");
        Assert.Equal("done", await run);
    }

    [Fact]
    public async Task StopsPingingOnceTheWorkFinishes()
    {
        int pings = 0;

        await AckProgressPinger.RunWithProgressAsync(
            () => Task.FromResult(1),
            _ => { Interlocked.Increment(ref pings); return Task.CompletedTask; },
            FastInterval);
        int afterRun = Volatile.Read(ref pings);
        await Task.Delay(FastInterval * 5);

        Assert.Equal(0, afterRun);
        Assert.Equal(0, Volatile.Read(ref pings));
    }

    [Fact]
    public async Task KeepsPingingAfterAFailedPing()
    {
        int pings = 0;
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => Interlocked.Increment(ref pings) == 1
                ? throw new CompanionApiException("Not found", 404)
                : Task.CompletedTask,
            FastInterval);

        while (Volatile.Read(ref pings) < 2)
        {
            await Task.Delay(5);
        }

        release.SetResult(true);
        Assert.True(await run);
    }

    [Fact]
    public async Task PropagatesTheWorksFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AckProgressPinger.RunWithProgressAsync<int>(
            () => throw new InvalidOperationException("boom"),
            _ => Task.CompletedTask,
            FastInterval));
    }
}
