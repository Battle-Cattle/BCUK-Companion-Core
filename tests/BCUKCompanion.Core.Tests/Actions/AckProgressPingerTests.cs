using BCUKCompanion.Core.Actions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public class AckProgressPingerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A fake clock that also signals each time the pinger starts waiting for its next ping, so a
    /// test can advance time exactly when there's a wait to run out instead of polling or sleeping.
    /// </summary>
    private sealed class PingClock : TimeProvider
    {
        private readonly FakeTimeProvider fake = new();
        private readonly SemaphoreSlim waitStarted = new(0);
        private int waitsStarted;

        public int WaitsStarted => Volatile.Read(ref waitsStarted);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = fake.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref waitsStarted);
            waitStarted.Release();
            return timer;
        }

        public override DateTimeOffset GetUtcNow() => fake.GetUtcNow();

        public override long GetTimestamp() => fake.GetTimestamp();

        public override long TimestampFrequency => fake.TimestampFrequency;

        public async Task ElapseNextWaitAsync()
        {
            Assert.True(await waitStarted.WaitAsync(TestTimeout), "The pinger never started its next wait.");
            fake.Advance(Interval);
        }

        public void Advance(TimeSpan delta) => fake.Advance(delta);
    }

    /// <summary>Counts pings and lets a test wait for each one.</summary>
    private sealed class PingLog
    {
        private readonly SemaphoreSlim pinged = new(0);
        private int count;

        public int Count => Volatile.Read(ref count);

        public int Record()
        {
            int n = Interlocked.Increment(ref count);
            pinged.Release();
            return n;
        }

        public async Task ElapseAndWaitForPingsAsync(PingClock clock, int pings)
        {
            for (int i = 0; i < pings; i++)
            {
                await clock.ElapseNextWaitAsync();
                Assert.True(await pinged.WaitAsync(TestTimeout), $"Timed out waiting for ping {i + 1}.");
            }
        }
    }

    [Fact]
    public async Task PingsWhileTheWorkRuns_AndReturnsItsResult()
    {
        var clock = new PingClock();
        var pings = new PingLog();
        var release = new TaskCompletionSource<string>();

        Task<string> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => { pings.Record(); return Task.FromResult(true); },
            Interval,
            clock);

        await pings.ElapseAndWaitForPingsAsync(clock, 3);

        release.SetResult("done");
        Assert.Equal("done", await run);
        Assert.Equal(3, pings.Count);
    }

    [Fact]
    public async Task StopsPingingOnceTheWorkFinishes()
    {
        var clock = new PingClock();
        var pings = new PingLog();

        await AckProgressPinger.RunWithProgressAsync(
            () => Task.FromResult(1),
            _ => { pings.Record(); return Task.FromResult(true); },
            Interval,
            clock);
        clock.Advance(Interval * 5);

        Assert.Equal(0, pings.Count);
    }

    [Fact]
    public async Task KeepsPingingAfterAFailedPing()
    {
        var clock = new PingClock();
        var pings = new PingLog();
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => pings.Record() == 1
                ? throw new CompanionApiException("Server error", 500)
                : Task.FromResult(true),
            Interval,
            clock);

        await pings.ElapseAndWaitForPingsAsync(clock, 2);

        release.SetResult(true);
        Assert.True(await run);
    }

    [Fact]
    public async Task StopsPingingOnceTheBotIsNoLongerWaiting()
    {
        var clock = new PingClock();
        var pings = new PingLog();
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => Task.FromResult(pings.Record() < 2),
            Interval,
            clock);

        await pings.ElapseAndWaitForPingsAsync(clock, 2);
        clock.Advance(Interval * 5);
        release.SetResult(true);

        Assert.True(await run);
        Assert.Equal(2, pings.Count);
        // No wait was started after the second ping, so no further ping could have been sent.
        Assert.Equal(2, clock.WaitsStarted);
    }

    [Fact]
    public async Task StopsPingingOnA404()
    {
        var clock = new PingClock();
        var pings = new PingLog();
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ =>
            {
                pings.Record();
                throw new CompanionApiException("Not found", 404);
            },
            Interval,
            clock);

        await pings.ElapseAndWaitForPingsAsync(clock, 1);
        clock.Advance(Interval * 5);
        release.SetResult(true);

        Assert.True(await run);
        Assert.Equal(1, pings.Count);
        Assert.Equal(1, clock.WaitsStarted);
    }

    [Fact]
    public async Task PropagatesTheWorksFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AckProgressPinger.RunWithProgressAsync<int>(
            () => throw new InvalidOperationException("boom"),
            _ => Task.FromResult(true),
            Interval,
            new PingClock()));
    }

    [Fact]
    public async Task WithoutATimeProvider_UsesTheSystemClock()
    {
        int pings = 0;
        var pinged = new TaskCompletionSource();
        var release = new TaskCompletionSource<bool>();

        Task<bool> run = AckProgressPinger.RunWithProgressAsync(
            () => release.Task,
            _ => { Interlocked.Increment(ref pings); pinged.TrySetResult(); return Task.FromResult(true); },
            TimeSpan.FromMilliseconds(1));

        try
        {
            await pinged.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            // Stop the ping loop, and wait for it to stop, even if the wait timed out.
            release.TrySetResult(true);
            await run;
        }

        Assert.True(await run);
        Assert.True(Volatile.Read(ref pings) >= 1);
    }

    [Fact]
    public async Task RejectsANullTimeProvider()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => AckProgressPinger.RunWithProgressAsync(
            () => Task.FromResult(true),
            _ => Task.FromResult(true),
            Interval,
            null!));
    }
}
