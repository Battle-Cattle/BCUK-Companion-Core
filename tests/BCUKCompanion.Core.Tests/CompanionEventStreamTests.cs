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
            data: {"type":"follow","displayName":"SomeViewer","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}

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
              {"type":"sub","displayName":"Alice","detail":null,"occurredAt":"2026-01-01T00:00:00Z"},
              {"type":"raid","displayName":"Bob","detail":"50 raiders","occurredAt":"2026-01-01T00:01:00Z"}
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

    [Fact]
    public async Task BackfillIgnoresOkFalseResponse()
    {
        const string recent = """{"ok":false,"events":[{"type":"sub","displayName":"Alice","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}]}""";
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
              {"type":"sub","displayName":"","detail":null,"occurredAt":"2026-01-01T00:00:00Z"},
              {"type":"unknown_future_type","displayName":"Someone","detail":null,"occurredAt":"2026-01-01T00:01:00Z"},
              {"type":"raid","displayName":"Bob","detail":"50 raiders","occurredAt":"2026-01-01T00:02:00Z"}
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
    public async Task ReconnectBackfillDoesNotDropADistinctEventSharingTheLiveWatermarkTimestamp()
    {
        const string sameTimestamp = "2026-01-01T00:00:00Z";
        string firstConnectionSse = "data: {\"type\":\"sub\",\"displayName\":\"Alice\",\"detail\":null,\"occurredAt\":\"" + sameTimestamp + "\"}\n\n";
        string secondConnectionRecent = """
            {"ok":true,"events":[
              {"type":"sub","displayName":"Alice","detail":null,"occurredAt":"REPLACE"},
              {"type":"sub","displayName":"Bob","detail":null,"occurredAt":"REPLACE"}
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
