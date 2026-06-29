using System.Diagnostics;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class CompanionClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static CompanionClient CreateClient(FakeTokenStore tokenStore) =>
        new(new Uri("https://bot.example.com"), tokenStore, new HttpClient());

    [Fact]
    public void IsLoggedInReflectsTokenStoreState()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);

        Assert.False(client.IsLoggedIn);

        client.SetManualToken("abc123");
        Assert.True(client.IsLoggedIn);
    }

    [Fact]
    public void LogoutClearsToken()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        client.Logout();

        Assert.False(client.IsLoggedIn);
        Assert.Null(tokenStore.Load());
    }

    [Fact]
    public void StartListeningWithoutTokenThrows()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);

        Assert.Throws<InvalidOperationException>(() => client.StartListening());
    }

    [Fact]
    public void StopListeningWithoutStartingIsNoOp()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);

        client.StopListening();
    }

    [Fact]
    public async Task StopListeningDoesNotBlockWaitingForTheLoopToFinish()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        var loopStarted = new TaskCompletionSource();
        var loopRelease = new TaskCompletionSource();
        client.ListenLoopOverride = async (_, _) =>
        {
            loopStarted.TrySetResult();
            // Never observes cancellation -- a real implementation that
            // blocked on this task inside StopListening() would hang here.
            await loopRelease.Task.ConfigureAwait(false);
        };

        client.StartListening();
        await loopStarted.Task.WaitAsync(TestTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Run the call itself on a time-bounded background task: if
            // StopListening() ever regresses into blocking, this fails fast
            // with a timeout instead of hanging the test run.
            await Task.Run(() => client.StopListening()).WaitAsync(TestTimeout);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"StopListening() took {stopwatch.Elapsed}, expected it to return immediately.");
        }
        finally
        {
            loopRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task StopListeningCalledFromWithinTheLoopDoesNotDeadlock()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        var loopFinished = new TaskCompletionSource();
        client.ListenLoopOverride = async (_, _) =>
        {
            // Ensure this runs as a genuine continuation (e.g. on a thread-pool
            // thread), the same way a ConnectionStateChanged/RedemptionReceived
            // handler invoked from inside the real SSE read loop would.
            await Task.Yield();
            client.StopListening();
            loopFinished.TrySetResult();
        };

        client.StartListening();

        await loopFinished.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RestartWaitsForThePreviousLoopToFullyStopBeforeStartingANewOne()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        var firstLoopStarted = new TaskCompletionSource();
        var firstLoopRelease = new TaskCompletionSource();
        var secondLoopStarted = new TaskCompletionSource();
        var sync = new object();
        int activeLoops = 0;
        int maxConcurrentLoops = 0;
        int callCount = 0;

        client.ListenLoopOverride = async (_, ct) =>
        {
            lock (sync)
            {
                activeLoops++;
                maxConcurrentLoops = Math.Max(maxConcurrentLoops, activeLoops);
            }

            try
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstLoopStarted.TrySetResult();
                    // Only unwinds once the test releases it -- simulates a
                    // loop that's slow to notice cancellation.
                    await firstLoopRelease.Task.ConfigureAwait(false);
                }
                else
                {
                    secondLoopStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (sync)
                {
                    activeLoops--;
                }
            }
        };

        client.StartListening();
        await firstLoopStarted.Task.WaitAsync(TestTimeout);

        try
        {
            // Same as above: bound the restart call itself so a regression
            // that makes it block fails fast instead of hanging the test.
            await Task.Run(() => client.StartListening()).WaitAsync(TestTimeout);
        }
        finally
        {
            firstLoopRelease.TrySetResult();
        }

        await secondLoopStarted.Task.WaitAsync(TestTimeout);

        Assert.Equal(1, maxConcurrentLoops);

        client.StopListening();
    }

    [Fact]
    public async Task ListenLoopFaultedIsRaisedWhenTheLoopThrows()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        var faultObserved = new TaskCompletionSource<Exception>();
        client.ListenLoopFaulted += (_, ex) => faultObserved.TrySetResult(ex);

        var thrown = new InvalidOperationException("boom");
        client.ListenLoopOverride = (_, _) => Task.FromException(thrown);

        client.StartListening();

        Exception observed = await faultObserved.Task.WaitAsync(TestTimeout);
        Assert.Same(thrown, observed);
    }

    [Fact]
    public async Task ListenLoopFaultedIsNotRaisedForExpectedCancellation()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        var faulted = false;
        client.ListenLoopFaulted += (_, _) => faulted = true;

        var loopStarted = new TaskCompletionSource();
        client.ListenLoopOverride = async (_, ct) =>
        {
            loopStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        };

        client.StartListening();
        await loopStarted.Task.WaitAsync(TestTimeout);

        client.StopListening();

        await client.CurrentLoopTask.WaitAsync(TestTimeout);

        Assert.False(faulted);
    }
}
