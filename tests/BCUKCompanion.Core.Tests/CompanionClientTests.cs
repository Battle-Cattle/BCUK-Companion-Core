using System.Diagnostics;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class CompanionClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static CompanionClient CreateClient(BCUKCompanion.Core.Tokens.ITokenStore tokenStore) =>
        new(new Uri("https://bot.example.com"), tokenStore, new HttpClient());

    [Fact]
    public void ThrowsForNonHttpsBotHost()
    {
        var tokenStore = new FakeTokenStore();

        var ex = Assert.Throws<ArgumentException>(
            () => new CompanionClient(new Uri("http://bot.example.com"), tokenStore, new HttpClient()));

        Assert.Equal("botHost", ex.ParamName);
    }

    [Fact]
    public void ThrowsForRelativeBotHost()
    {
        var tokenStore = new FakeTokenStore();

        var ex = Assert.Throws<ArgumentException>(
            () => new CompanionClient(new Uri("bot.example.com", UriKind.Relative), tokenStore, new HttpClient()));

        Assert.Equal("botHost", ex.ParamName);
    }

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
    public async Task LogoutAsyncClearsTheTokenAndRevokesItOnTheBot()
    {
        var tokenStore = new FakeTokenStore();
        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.OK, """{"ok":true}""");
        using var client = new CompanionClient(new Uri("https://bot.example.com"), tokenStore, new HttpClient(handler));
        client.SetManualToken("abc123");

        Assert.Equal(LogoutResult.Revoked, await client.LogoutAsync());

        Assert.Null(tokenStore.Load());
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/companion/token/revoke", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("abc123", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task LogoutAsyncStillLogsOutLocallyWhenRevokingFails()
    {
        var tokenStore = new FakeTokenStore();
        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.NotFound, "<html>Page not found.</html>");
        using var client = new CompanionClient(new Uri("https://bot.example.com"), tokenStore, new HttpClient(handler));
        client.SetManualToken("abc123");

        Assert.Equal(LogoutResult.NotConfirmed, await client.LogoutAsync());
        Assert.Null(tokenStore.Load());
    }

    [Fact]
    public async Task LogoutAsyncCountsAnAlreadyRevokedTokenAsRevoked()
    {
        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.Unauthorized, """{"ok":false}""");
        using var client = new CompanionClient(new Uri("https://bot.example.com"), new FakeTokenStore(), new HttpClient(handler));
        client.SetManualToken("abc123");

        Assert.Equal(LogoutResult.Revoked, await client.LogoutAsync());
        Assert.False(client.IsLoggedIn);
    }

    [Fact]
    public async Task LogoutAsyncWithNoTokenDoesNotCallTheBot()
    {
        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.OK, "{}");
        using var client = new CompanionClient(new Uri("https://bot.example.com"), new FakeTokenStore(), new HttpClient(handler));

        Assert.Equal(LogoutResult.NoToken, await client.LogoutAsync());
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void LogoutAsyncThrowsATokenFileErrorBeforeReturningATask()
    {
        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.OK, "{}");
        using var client = new CompanionClient(new Uri("https://bot.example.com"), new ThrowingClearTokenStore("abc123"), new HttpClient(handler));

        Assert.Throws<IOException>(() => { _ = client.LogoutAsync(); });
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task DiscardRejectedTokenClearsTheTokenTheLoopWasUsing()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("old");
        var discarded = new TaskCompletionSource<bool>();
        client.ListenLoopOverride = (_, _) =>
        {
            discarded.TrySetResult(client.DiscardRejectedToken());
            return Task.CompletedTask;
        };

        client.StartListening();

        Assert.True(await discarded.Task.WaitAsync(TestTimeout));
        Assert.Null(tokenStore.Load());
    }

    [Fact]
    public async Task DiscardRejectedTokenKeepsATokenSavedSinceTheLoopStarted()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("old");
        var discarded = new TaskCompletionSource<bool>();
        client.ListenLoopOverride = (_, _) =>
        {
            // The user logged in again while this loop was still connecting with the old token.
            tokenStore.Save("new");
            discarded.TrySetResult(client.DiscardRejectedToken());
            return Task.CompletedTask;
        };

        client.StartListening();

        Assert.False(await discarded.Task.WaitAsync(TestTimeout));
        Assert.Equal("new", tokenStore.Load());
    }

    [Fact]
    public async Task DiscardRejectedTokenKeepsATokenSavedWhileItWasClearing()
    {
        var tokenStore = new GatedClearTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("old");
        var discard = new TaskCompletionSource<Task<bool>>();
        client.ListenLoopOverride = (_, _) =>
        {
            // Kept running until the discard finishes, as a real loop is while its handler runs.
            Task<bool> discarding = Task.Run(client.DiscardRejectedToken);
            discard.TrySetResult(discarding);
            return discarding;
        };

        client.StartListening();
        Task<bool> discarding = await discard.Task.WaitAsync(TestTimeout);
        await tokenStore.ClearEntered.Task.WaitAsync(TestTimeout);

        // A login finishing on another thread while the rejected token is being deleted.
        Task saving = Task.Run(() => client.SetManualToken("new"));
        // A negative wait: there's no signal for "blocked on the lock", so give a save that
        // wrongly skipped the lock time to finish. The check can only miss the bug, never flake.
        await Task.Delay(100);
        Assert.False(saving.IsCompleted);
        tokenStore.ReleaseClear.Set();

        Assert.True(await discarding.WaitAsync(TestTimeout));
        await saving.WaitAsync(TestTimeout);
        Assert.Equal("new", tokenStore.Load());
    }

    [Fact]
    public void DiscardRejectedTokenOutsideAListenLoopDoesNothing()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        Assert.False(client.DiscardRejectedToken());
        Assert.Equal("abc123", tokenStore.Load());
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

/// <summary>A token store whose <see cref="Clear"/> blocks until released, to hold a clear in progress.</summary>
internal sealed class GatedClearTokenStore : BCUKCompanion.Core.Tokens.ITokenStore
{
    private volatile string? _token;

    public TaskCompletionSource ClearEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualResetEventSlim ReleaseClear { get; } = new();

    public string? Load() => _token;

    public void Save(string token) => _token = token;

    public void Clear()
    {
        ClearEntered.TrySetResult();
        ReleaseClear.Wait();
        _token = null;
    }
}

internal sealed class ThrowingClearTokenStore(string token) : BCUKCompanion.Core.Tokens.ITokenStore
{
    public string? Load() => token;

    public void Save(string newToken) => throw new IOException("The token file is locked.");

    public void Clear() => throw new IOException("The token file is locked.");
}
