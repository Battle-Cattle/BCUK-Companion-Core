using System.Net;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class CompanionEventStreamTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RaisesRedemptionReceivedForRedemptionPayload()
    {
        const string sse = """
            data: {"type":"channel_points_redemption","rewardId":"r1","rewardTitle":"Hydrate!","userLogin":"u","userName":"U","userInput":"","redeemedAt":"2026-01-01T00:00:00Z"}

            """;
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: """{"ok":true,"events":[]}""");
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        RedemptionEvent? received = null;
        stream.RedemptionReceived += (_, e) => received = e;

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received is not null, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.NotNull(received);
        Assert.Equal("Hydrate!", received!.RewardTitle);
    }

    [Fact]
    public async Task RaisesActivityReceivedForFollowPayload()
    {
        const string sse = """
            data: {"type":"follow","id":1,"displayName":"SomeViewer","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}

            """;
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: """{"ok":true,"events":[]}""");
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        CompanionActivityEvent? received = null;
        stream.ActivityReceived += (_, e) => received = e;

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received is not null, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.NotNull(received);
        Assert.Equal("follow", received!.Type);
        Assert.Equal("SomeViewer", received.DisplayName);
    }

    [Fact]
    public async Task IgnoresUnknownEventTypeButStillProcessesLaterEvents()
    {
        const string sse = """
            data: {"type":"future_event","foo":"bar"}

            data: {"type":"channel_points_redemption","rewardId":"r1","rewardTitle":"t","userLogin":"u","userName":"U","redeemedAt":"2026-01-01T00:00:00Z"}

            """;
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: """{"ok":true,"events":[]}""");
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var activityRaised = false;
        stream.ActivityReceived += (_, _) => activityRaised = true;
        RedemptionEvent? redemption = null;
        stream.RedemptionReceived += (_, e) => redemption = e;

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => redemption is not null, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.False(activityRaised);
        Assert.NotNull(redemption);
    }

    [Fact]
    public async Task BackfillsRecentActivityOnConnect()
    {
        const string recent = """
            {"ok":true,"events":[
              {"type":"sub","id":1,"displayName":"Alice","detail":null,"occurredAt":"2026-01-01T00:00:00Z"},
              {"type":"raid","id":2,"displayName":"Bob","detail":"50 raiders","occurredAt":"2026-01-01T00:01:00Z"}
            ]}
            """;
        var handler = new RoutingHttpMessageHandler(sseBody: string.Empty, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var received = new List<CompanionActivityEvent>();
        stream.ActivityReceived += (_, e) => received.Add(e);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 2, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.Equal(2, received.Count);
        Assert.Equal("Alice", received[0].DisplayName);
        Assert.Equal("Bob", received[1].DisplayName);
        Assert.Equal("/api/companion/events/recent", handler.LastRecentRequestPath);
    }

    /// <summary>
    /// A host-seeded <see cref="CompanionEventStream.LastActivityEventId"/> (from a previous run)
    /// suppresses catch-up events up to it, delivers newer ones, and reports the new mark.
    /// </summary>
    [Fact]
    public async Task SeededLastActivityEventIdSkipsAlreadyShownBackfill()
    {
        const string recent = """
            {"ok":true,"events":[
              {"type":"follow","id":7,"displayName":"LastSession","detail":null,"occurredAt":"2026-01-01T00:00:00Z"},
              {"type":"follow","id":8,"displayName":"WhileClosed","detail":null,"occurredAt":"2026-01-01T00:01:00Z"}
            ]}
            """;
        var handler = new RoutingHttpMessageHandler(sseBody: string.Empty, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"))
        {
            LastActivityEventId = 7,
        };

        var received = new List<CompanionActivityEvent>();
        var changes = new List<long>();
        stream.ActivityReceived += (_, e) => received.Add(e);
        stream.LastActivityEventIdChanged += (_, id) => changes.Add(id);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 1, TestTimeout);
        await Task.Delay(50);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        CompanionActivityEvent activity = Assert.Single(received);
        Assert.Equal("WhileClosed", activity.DisplayName);
        Assert.Equal(new long[] { 8 }, changes);
        Assert.Equal(8, stream.LastActivityEventId);
    }

    /// <summary>
    /// With no saved mark, <see cref="CompanionEventStream.BaselineOnFirstCatchUp"/> makes the
    /// first catch-up record the newest id silently; a later live event is still delivered.
    /// </summary>
    [Fact]
    public async Task BaselineOnFirstCatchUpRecordsNewestIdWithoutAnnouncingThenDeliversLiveEvents()
    {
        const string recent = """
            {"ok":true,"events":[
              {"type":"follow","id":3,"displayName":"Old","detail":null,"occurredAt":"2026-01-01T00:00:00Z"},
              {"type":"follow","id":5,"displayName":"LastFollower","detail":null,"occurredAt":"2026-01-01T00:01:00Z"}
            ]}
            """;
        const string sse = "data: {\"type\":\"follow\",\"id\":6,\"displayName\":\"NewFollower\",\"detail\":null,\"occurredAt\":\"2026-01-01T00:02:00Z\"}\n\n";
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"))
        {
            BaselineOnFirstCatchUp = true,
        };

        var received = new List<CompanionActivityEvent>();
        var changes = new List<long>();
        stream.ActivityReceived += (_, e) => received.Add(e);
        stream.LastActivityEventIdChanged += (_, id) => changes.Add(id);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 1, TestTimeout);
        await Task.Delay(50);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        CompanionActivityEvent activity = Assert.Single(received);
        Assert.Equal("NewFollower", activity.DisplayName);
        Assert.Equal(new long[] { 5, 6 }, changes);
        Assert.False(stream.BaselineOnFirstCatchUp);
    }

    /// <summary>
    /// The new mark is reported only after the activity itself, so a host persisting it never
    /// records an activity it wasn't handed.
    /// </summary>
    [Fact]
    public async Task LastActivityEventIdChangedIsRaisedAfterActivityReceived()
    {
        const string recent = """{"ok":true,"events":[{"type":"follow","id":4,"displayName":"Alice","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}]}""";
        var handler = new RoutingHttpMessageHandler(sseBody: string.Empty, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var order = new List<string>();
        stream.ActivityReceived += (_, _) => order.Add("activity");
        stream.LastActivityEventIdChanged += (_, _) => order.Add("mark");

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => order.Count >= 2, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.Equal(new[] { "activity", "mark" }, order);
    }

    [Fact]
    public async Task ActivityPresentInBothInitialBackfillAndInitialSseBodyIsDispatchedOnce()
    {
        const string sameTimestamp = "2026-01-01T00:00:00Z";
        const string recent = """{"ok":true,"events":[{"type":"sub","id":1,"displayName":"Alice","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}]}""";
        string sse = "data: {\"type\":\"sub\",\"id\":1,\"displayName\":\"Alice\",\"detail\":null,\"occurredAt\":\"" + sameTimestamp + "\"}\n\n";
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var received = new List<CompanionActivityEvent>();
        stream.ActivityReceived += (_, e) => received.Add(e);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 1, TestTimeout);
        // Give a would-be duplicate live dispatch a moment to have fired if it were going to.
        await Task.Delay(50);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        CompanionActivityEvent activity = Assert.Single(received);
        Assert.Equal("Alice", activity.DisplayName);
    }

    [Fact]
    public async Task BackfillIgnoresOkFalseResponse()
    {
        const string recent = """{"ok":false,"events":[{"type":"sub","id":1,"displayName":"Alice","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}]}""";
        var handler = new RoutingHttpMessageHandler(sseBody: string.Empty, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var activityRaised = false;
        stream.ActivityReceived += (_, _) => activityRaised = true;

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => handler.LastRecentRequestPath is not null, TestTimeout);
        // Give the (deliberately absent) ActivityReceived dispatch a moment to have fired if it were going to.
        await Task.Delay(50);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.False(activityRaised);
    }

    [Fact]
    public async Task BackfillDiscardsRecordsFailingLiveEventValidation()
    {
        const string recent = """
            {"ok":true,"events":[
              {"type":"sub","id":1,"displayName":"","detail":null,"occurredAt":"2026-01-01T00:00:00Z"},
              {"type":"unknown_future_type","id":2,"displayName":"Someone","detail":null,"occurredAt":"2026-01-01T00:01:00Z"},
              {"type":"giftsub","displayName":"NoId","detail":null,"occurredAt":"2026-01-01T00:01:30Z"},
              {"type":"raid","id":3,"displayName":"Bob","detail":"50 raiders","occurredAt":"2026-01-01T00:02:00Z"}
            ]}
            """;
        var handler = new RoutingHttpMessageHandler(sseBody: string.Empty, recentEventsBody: recent);
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var received = new List<CompanionActivityEvent>();
        stream.ActivityReceived += (_, e) => received.Add(e);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 1, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        CompanionActivityEvent activity = Assert.Single(received);
        Assert.Equal("Bob", activity.DisplayName);
    }

    [Fact]
    public async Task ReconnectBackfillDoesNotDropADistinctEventSharingTheLiveWatermarkId()
    {
        const string sameTimestamp = "2026-01-01T00:00:00Z";
        string firstConnectionSse = "data: {\"type\":\"sub\",\"id\":1,\"displayName\":\"Alice\",\"detail\":null,\"occurredAt\":\"" + sameTimestamp + "\"}\n\n";
        string secondConnectionRecent = """
            {"ok":true,"events":[
              {"type":"sub","id":1,"displayName":"Alice","detail":null,"occurredAt":"REPLACE"},
              {"type":"sub","id":2,"displayName":"Bob","detail":null,"occurredAt":"REPLACE"}
            ]}
            """.Replace("REPLACE", sameTimestamp);
        var handler = new SequencedRoutingHttpMessageHandler(
            sseBodies: new[]
            {
                firstConnectionSse,
                string.Empty,
            },
            recentBodies: new[]
            {
                """{"ok":true,"events":[]}""",
                secondConnectionRecent,
            });
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var received = new List<CompanionActivityEvent>();
        stream.ActivityReceived += (_, e) => received.Add(e);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 2, TimeSpan.FromSeconds(10));
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.Equal(2, received.Count);
        Assert.Equal("Alice", received[0].DisplayName);
        Assert.Equal("Bob", received[1].DisplayName);
        Assert.Equal(received[0].OccurredAt, received[1].OccurredAt);
    }

    [Fact]
    public async Task NonNetworkExceptionFromConnectIsNotSilentlySwallowed()
    {
        // A NullReferenceException (or any other non-network exception) reaching the
        // connect-attempt catch is a bug, not a transient network failure -- it must
        // propagate out of RunAsync rather than being treated identically to a dropped
        // connection (silently retried forever with no diagnostic trail).
        var handler = new ThrowingHttpMessageHandler(() => new NullReferenceException("boom"));
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        using var cts = new CancellationTokenSource(TestTimeout);
        Task runTask = stream.RunAsync("token", cts.Token);

        NullReferenceException thrown = await Assert.ThrowsAsync<NullReferenceException>(() => runTask);
        Assert.Equal("boom", thrown.Message);
        // Only ever attempted once -- a caught-and-retried exception would have driven the
        // handler to be invoked repeatedly within the test timeout instead of faulting RunAsync.
        Assert.Equal(1, handler.InvocationCount);
    }

    [Fact]
    public async Task RaisesAdvancedCommandReceivedOncePerEventId()
    {
        const string sse = """
            data: {"type":"advanced_command","id":"e1","commandId":7,"commandName":"Hydrate","source":"chat","userLogin":"alice","userName":"Alice","args":"now","pointsSpent":100,"parameters":{"color":"red"},"ackRequired":true,"triggeredAt":"2026-01-01T00:00:00Z"}

            data: {"type":"advanced_command","id":"e1","commandId":7,"commandName":"Hydrate","source":"chat","userLogin":"alice","userName":"Alice","args":"now","pointsSpent":100,"parameters":{"color":"red"},"ackRequired":true,"triggeredAt":"2026-01-01T00:00:00Z"}

            data: {"type":"advanced_command","id":"e2","commandId":7,"commandName":"Hydrate","source":"streamdeck","userLogin":null,"userName":null,"args":null,"pointsSpent":0,"parameters":{},"ackRequired":true,"triggeredAt":"2026-01-01T00:00:01Z"}

            """;
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: """{"ok":true,"events":[]}""");
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var received = new List<AdvancedCommandEvent>();
        stream.AdvancedCommandReceived += (_, e) => received.Add(e);

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => received.Count >= 2, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.Equal(["e1", "e2"], received.Select(e => e.Id));
        Assert.Equal("Hydrate", received[0].CommandName);
        Assert.Equal("red", received[0].Parameters!["color"]);
        Assert.True(received[0].AckRequired);
        Assert.Null(received[1].UserLogin);
    }

    [Fact]
    public async Task IgnoresIncompleteAdvancedCommandPayloads()
    {
        const string sse = """
            data: {"type":"advanced_command","id":"","commandName":"Hydrate","source":"chat","triggeredAt":"2026-01-01T00:00:00Z"}

            data: {"type":"advanced_command","id":"e1","commandName":"Hydrate","source":"somewhere","triggeredAt":"2026-01-01T00:00:00Z"}

            data: {"type":"advanced_command","id":"e2","commandName":"","source":"chat","triggeredAt":"2026-01-01T00:00:00Z"}

            data: {"type":"advanced_command", not json

            data: {"type":"channel_points_redemption","rewardId":"r1","rewardTitle":"t","userLogin":"u","userName":"U","redeemedAt":"2026-01-01T00:00:00Z"}

            """;
        var handler = new RoutingHttpMessageHandler(sse, recentEventsBody: """{"ok":true,"events":[]}""");
        var stream = new CompanionEventStream(new HttpClient(handler), new Uri("https://bot.example.com"));

        var commandRaised = false;
        stream.AdvancedCommandReceived += (_, _) => commandRaised = true;
        RedemptionEvent? redemption = null;
        stream.RedemptionReceived += (_, e) => redemption = e;

        using var cts = new CancellationTokenSource();
        Task runTask = stream.RunAsync("token", cts.Token);
        await WaitUntilAsync(() => redemption is not null, TestTimeout);
        cts.Cancel();
        await SwallowCancellationAsync(runTask);

        Assert.False(commandRaised);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static async Task SwallowCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Routes GET /api/companion/events/recent to a fixed backfill body and every
    /// other request (the SSE stream itself) to a fixed SSE body.
    /// </summary>
    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _sseBody;
        private readonly string _recentEventsBody;

        public string? LastRecentRequestPath { get; private set; }

        public RoutingHttpMessageHandler(string sseBody, string recentEventsBody)
        {
            _sseBody = sseBody;
            _recentEventsBody = recentEventsBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/companion/events/recent")
            {
                LastRecentRequestPath = request.RequestUri.AbsolutePath;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_recentEventsBody),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sseBody),
            });
        }
    }

    /// <summary>
    /// Throws the exception produced by <paramref name="exceptionFactory"/> from every
    /// SendAsync call, counting how many times it was invoked -- used to assert a non-network
    /// exception propagates rather than being caught and retried.
    /// </summary>
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _exceptionFactory;

        public int InvocationCount { get; private set; }

        public ThrowingHttpMessageHandler(Func<Exception> exceptionFactory)
        {
            _exceptionFactory = exceptionFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw _exceptionFactory();
        }
    }

    /// <summary>
    /// Like <see cref="RoutingHttpMessageHandler"/>, but hands out a different body to each
    /// successive request against a given path (dequeuing from the supplied lists), so a test
    /// can simulate what a reconnect sees differently from the first connection. The last body
    /// in a list is reused once its queue is exhausted.
    /// </summary>
    private sealed class SequencedRoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _sseBodies;
        private readonly Queue<string> _recentBodies;
        private string _lastSseBody = string.Empty;
        private string _lastRecentBody = """{"ok":true,"events":[]}""";

        public SequencedRoutingHttpMessageHandler(IEnumerable<string> sseBodies, IEnumerable<string> recentBodies)
        {
            _sseBodies = new Queue<string>(sseBodies);
            _recentBodies = new Queue<string>(recentBodies);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/companion/events/recent")
            {
                if (_recentBodies.Count > 0)
                {
                    _lastRecentBody = _recentBodies.Dequeue();
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_lastRecentBody),
                });
            }

            if (_sseBodies.Count > 0)
            {
                _lastSseBody = _sseBodies.Dequeue();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_lastSseBody),
            });
        }
    }
}
