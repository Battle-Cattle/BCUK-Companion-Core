using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public class AckProgressPingerTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Waits until <paramref name="pings"/> reaches <paramref name="target"/>, failing (not hanging) after <see cref="TestTimeout"/>.</summary>
    private static async Task WaitForPingsAsync(Func<int> pings, int target)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeout;
        while (pings() < target)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {target} pings (got {pings()}).");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task PingsWhileTheWorkRuns_AndReturnsItsResult()
    {
        int pings = 0;
        var release = new TaskCompletionSource<string>();

        Task<string> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => { Interlocked.Increment(ref pings); return Task.FromResult(true); },
            FastInterval);

        await WaitForPingsAsync(() => Volatile.Read(ref pings), 3);

        release.SetResult("done");
        Assert.Equal("done", await run);
    }

    [Fact]
    public async Task StopsPingingOnceTheWorkFinishes()
    {
        int pings = 0;

        await AckProgressPinger.RunWithProgressAsync(
            () => Task.FromResult(1),
            _ => { Interlocked.Increment(ref pings); return Task.FromResult(true); },
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
                ? throw new CompanionApiException("Server error", 500)
                : Task.FromResult(true),
            FastInterval);

        await WaitForPingsAsync(() => Volatile.Read(ref pings), 2);

        release.SetResult(true);
        Assert.True(await run);
    }

    [Fact]
    public async Task StopsPingingOnceTheBotIsNoLongerWaiting()
    {
        int pings = 0;
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => Task.FromResult(Interlocked.Increment(ref pings) < 2),
            FastInterval);

        await WaitForPingsAsync(() => Volatile.Read(ref pings), 2);
        await Task.Delay(FastInterval * 5);

        Assert.Equal(2, Volatile.Read(ref pings));
        release.SetResult(true);
        Assert.True(await run);
    }

    [Fact]
    public async Task StopsPingingOnA404()
    {
        int pings = 0;
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ =>
            {
                Interlocked.Increment(ref pings);
                throw new CompanionApiException("Not found", 404);
            },
            FastInterval);

        await WaitForPingsAsync(() => Volatile.Read(ref pings), 1);
        await Task.Delay(FastInterval * 5);

        Assert.Equal(1, Volatile.Read(ref pings));
        release.SetResult(true);
        Assert.True(await run);
    }

    [Fact]
    public async Task PropagatesTheWorksFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AckProgressPinger.RunWithProgressAsync<int>(
            () => throw new InvalidOperationException("boom"),
            _ => Task.FromResult(true),
            FastInterval));
    }
}
