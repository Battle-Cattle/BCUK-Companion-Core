using System.Collections.Concurrent;
using System.Text.Json;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

public sealed class BotEventHandlerTests : IDisposable
{
    private readonly RecordingHttpHandler http = new();
    private readonly CompanionClient client;
    private readonly List<(string Title, string Text)> balloons = [];
    private readonly ConcurrentQueue<BotEventArgs> hostEvents = new();
    private readonly List<Task> queued = [];

    public BotEventHandlerTests()
    {
        client = new CompanionClient(new Uri("https://bot.example.com"), new MemoryTokenStore("token"), new HttpClient(http));
    }

    public void Dispose() => client.Dispose();

    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider clock = new();

    private BotEventHandler CreateHandler(CompanionTrayAppOptions? options = null) => new(
        options ?? new CompanionTrayAppOptions { OnBotEvent = hostEvents.Enqueue },
        work =>
        {
            Task task = work();
            queued.Add(task);
            return task;
        },
        (title, text) => { lock (balloons) { balloons.Add((title, text)); } },
        clock);

    /// <summary>Waits for every queued handler to finish, as the UI thread would run them.</summary>
    private Task DrainAsync() => Task.WhenAll(queued);

    private static Func<EventActionDispatcher> Dispatching(string name, EventActionTriggerKind kind, params IEventAction[] actions) =>
        () => new EventActionDispatcher(() => [new EventActionMapping(name, actions, kind)], () => new EmptyContext());

    private static RedemptionEvent Redemption(bool ackRequired, string id = "r-1") => new()
    {
        Type = "channel_points_redemption",
        Id = id,
        RewardId = "reward-1",
        RewardTitle = "Lights",
        UserLogin = "viewer",
        UserName = "Viewer",
        UserInput = "blue please",
        AckRequired = ackRequired,
        RedeemedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static AdvancedCommandEvent Command(bool ackRequired = true, string source = "chat") => new()
    {
        Type = "advanced_command",
        Id = "c-1",
        CommandId = 7,
        CommandName = "Hydrate",
        Source = source,
        UserLogin = "viewer",
        UserName = "",
        Args = "now",
        PointsSpent = 100,
        Parameters = new Dictionary<string, string> { ["color"] = "red" },
        AckRequired = ackRequired,
        TriggeredAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private (string Path, JsonElement Body) SingleAck()
    {
        var (path, body) = Assert.Single(http.Requests);
        Assert.NotNull(body);
        return (path, body.Value);
    }

    private static void AssertAck(JsonElement body, string eventId, bool ok, string? error)
    {
        Assert.Equal(eventId, body.GetProperty("eventId").GetString());
        Assert.Equal(ok, body.GetProperty("ok").GetBoolean());
        Assert.Equal(error, body.GetProperty("error").GetString());
    }

    /// <summary>An action whose every run waits until the test releases it, signalling when each run starts.</summary>
    private sealed class GatedAction : IEventAction
    {
        public SemaphoreSlim Started { get; } = new(0);

        public ConcurrentQueue<TaskCompletionSource> Runs { get; } = new();

        public string Kind => "gated";

        public string Describe(IEventActionContext context) => Kind;

        public IReadOnlyList<string> Validate(IEventActionContext context) => [];

        public async Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
        {
            var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Runs.Enqueue(run);
            Started.Release();
            await run.Task;
            return true;
        }

        public void FinishOldest()
        {
            Assert.True(Runs.TryDequeue(out TaskCompletionSource? run));
            run.SetResult();
        }
    }

    [Fact]
    public async Task OverlappingEvents_RunTheirActionsOneAtATimeInArrivalOrder()
    {
        var gated = new GatedAction();
        BotEventHandler handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Lights", EventActionTriggerKind.Reward, gated),
        });

        handler.OnRedemption(client, Redemption(ackRequired: false));
        handler.OnRedemption(client, Redemption(ackRequired: false));

        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await gated.Started.WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(2, balloons.Count); // Both events are announced straight away.

        gated.FinishOldest();
        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        gated.FinishOldest();
        await DrainAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task AnActivity_DoesNotWaitBehindAnotherEventsActions()
    {
        var gated = new GatedAction();
        var hostSaw = new ConcurrentQueue<string>();
        BotEventHandler handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Lights", EventActionTriggerKind.Reward, gated),
            OnBotEventAsync = e =>
            {
                hostSaw.Enqueue(e.EventName);
                return Task.FromResult<EventDispatchResult?>(null);
            },
        });

        handler.OnRedemption(client, Redemption(ackRequired: false));
        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        handler.OnActivity(new CompanionActivityEvent { Type = "follow", Id = 1, DisplayName = "Fan" });

        await WaitForAsync(() => hostSaw.Contains("activity.received"));
        gated.FinishOldest();
        await DrainAsync();
    }

    [Fact]
    public async Task AnActivitysTask_CompletesOnlyOnceTheHostsHandlerHas()
    {
        var hostDone = new TaskCompletionSource<EventDispatchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BotEventHandler handler = CreateHandler(new CompanionTrayAppOptions { OnBotEventAsync = _ => hostDone.Task });

        Task handled = handler.OnActivity(new CompanionActivityEvent { Type = "follow", Id = 1, DisplayName = "Fan" });
        await Task.Delay(50);
        Assert.False(handled.IsCompleted);

        hostDone.SetResult(null);
        await handled.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AQueuedEventTheBotStopsWaitingFor_IsSkippedAndAcknowledgedAsFailed()
    {
        var gated = new GatedAction();
        BotEventHandler handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Lights", EventActionTriggerKind.Reward, gated),
        });
        handler.OnRedemption(client, Redemption(ackRequired: false));
        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));

        // Queued behind the first; its first progress ping hears the bot isn't waiting any more.
        handler.OnRedemption(client, Redemption(ackRequired: true, id: "r-2"));
        clock.Advance(AckProgressPinger.DefaultInterval);
        await WaitForAsync(() => http.Requests.Any(r => r.Path == "/api/companion/rewards/progress"));
        gated.FinishOldest();
        await DrainAsync();

        Assert.False(await gated.Started.WaitAsync(TimeSpan.FromMilliseconds(100)));
        var ack = Assert.Single(http.Requests, r => r.Path == "/api/companion/rewards/ack");
        Assert.Equal("r-2", ack.Body!.Value.GetProperty("eventId").GetString());
        Assert.False(ack.Body.Value.GetProperty("ok").GetBoolean());
        Assert.Contains(balloons, b => b.Title == "Skipped Lights");
    }

    [Fact]
    public async Task AQueuedEventWhosePingsKeepFailing_IsSkippedOnceTheBotWouldHaveTimedItOut()
    {
        var failing = new RecordingHttpHandler(System.Net.HttpStatusCode.ServiceUnavailable, "{}");
        using var unreachable = new CompanionClient(new Uri("https://bot.example.com"), new MemoryTokenStore("token"), new HttpClient(failing));
        var gated = new GatedAction();
        BotEventHandler handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Lights", EventActionTriggerKind.Reward, gated),
        });
        handler.OnRedemption(unreachable, Redemption(ackRequired: false));
        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));

        handler.OnRedemption(unreachable, Redemption(ackRequired: true, id: "r-2"));
        clock.Advance(TimeSpan.FromMinutes(3));
        gated.FinishOldest();
        await DrainAsync();

        Assert.False(await gated.Started.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Contains(balloons, b => b.Title == "Skipped Lights");
    }

    [Fact]
    public async Task OverlappingEvents_RunAtOnceWhenTheHostOptsOut()
    {
        var gated = new GatedAction();
        BotEventHandler handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Lights", EventActionTriggerKind.Reward, gated),
            RunEventActionsConcurrently = true,
        });

        handler.OnRedemption(client, Redemption(ackRequired: false));
        handler.OnRedemption(client, Redemption(ackRequired: false));

        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        gated.FinishOldest();
        gated.FinishOldest();
        await DrainAsync();
    }

    [Fact]
    public async Task OnRedemption_WithoutAck_ShowsABalloonAndHandsTheHostTheEvent()
    {
        CreateHandler().OnRedemption(client, Redemption(ackRequired: false));
        await DrainAsync();

        Assert.Equal([("Lights", "Redeemed by Viewer")], balloons);
        var botEvent = Assert.Single(hostEvents);
        Assert.Equal(EventActionDispatcher.RedemptionEventName, botEvent.EventName);
        Assert.Equal("r-1", botEvent.Metadata["redemptionId"]);
        Assert.Equal("Lights", botEvent.Metadata["rewardTitle"]);
        Assert.Equal("blue please", botEvent.Metadata["userInput"]);
        Assert.Equal("2026-01-01T12:00:00.0000000+00:00", botEvent.Metadata["redeemedAt"]);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task OnRedemption_WithAckAndSucceedingActions_AcknowledgesSuccess()
    {
        var action = new StubAction();
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Lights", EventActionTriggerKind.Reward, action),
        });

        handler.OnRedemption(client, Redemption(ackRequired: true));
        await DrainAsync();

        Assert.Equal(1, action.Runs);
        var (path, body) = SingleAck();
        Assert.Equal("/api/companion/rewards/ack", path);
        AssertAck(body, "r-1", ok: true, error: null);
    }

    [Fact]
    public async Task OnRedemption_WithAckButNoIdFromAnOldBot_DoesNotAcknowledge()
    {
        var redemption = Redemption(ackRequired: true);
        redemption.Id = "";

        CreateHandler().OnRedemption(client, redemption);
        await DrainAsync();

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task OnAdvancedCommand_NothingMapped_AcknowledgesFailureSoTheViewerIsRefunded()
    {
        CreateHandler().OnAdvancedCommand(client, Command());
        await DrainAsync();

        var (path, body) = SingleAck();
        Assert.Equal("/api/companion/commands/ack", path);
        AssertAck(body, "c-1", ok: false, error: AdvancedCommandAck.NoMappingError);
    }

    [Fact]
    public async Task OnAdvancedCommand_AFailingAction_AcknowledgesFailureAndShowsIt()
    {
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Hydrate", EventActionTriggerKind.Command, new StubAction(), new StubAction(succeeds: false)),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        AssertAck(SingleAck().Body, "c-1", ok: false, error: "Action did not complete successfully.");
        Assert.Contains(("Action failed", "Action did not complete successfully."), balloons);
    }

    [Fact]
    public async Task OnAdvancedCommand_DispatcherCrashes_AcknowledgesFailure()
    {
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = () => throw new InvalidOperationException("config unreadable"),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        AssertAck(SingleAck().Body, "c-1", ok: false, error: "config unreadable");
        Assert.Contains(("Action dispatch crashed", "config unreadable"), balloons);
    }

    [Fact]
    public async Task OnAdvancedCommand_WithoutAck_RunsActionsButSendsNothing()
    {
        var action = new StubAction();
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            CreateEventActionDispatcher = Dispatching("Hydrate", EventActionTriggerKind.Command, action),
        });

        handler.OnAdvancedCommand(client, Command(ackRequired: false));
        await DrainAsync();

        Assert.Equal(1, action.Runs);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task OnAdvancedCommand_HostHandlerRanActionsAndNothingMapped_AcknowledgesTheHostsResult()
    {
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            OnBotEventAsync = e => Task.FromResult<EventDispatchResult?>(
                new EventDispatchResult("Hydrate", [new EventActionResult(new StubAction(), true, null)])),
            CreateEventActionDispatcher = Dispatching("Something else", EventActionTriggerKind.Command, new StubAction()),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        AssertAck(SingleAck().Body, "c-1", ok: true, error: null);
    }

    [Fact]
    public async Task OnAdvancedCommand_HostHandlerThrows_FailsEvenThoughTheMappedActionsRan()
    {
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            OnBotEventAsync = _ => throw new InvalidOperationException("host broke"),
            CreateEventActionDispatcher = Dispatching("Hydrate", EventActionTriggerKind.Command, new StubAction()),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        AssertAck(SingleAck().Body, "c-1", ok: false, error: "host broke");
    }

    [Fact]
    public async Task OnAdvancedCommand_HostHandlerRanNothing_AcknowledgesTheMappedActions()
    {
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            OnBotEventAsync = _ => Task.FromResult<EventDispatchResult?>(null),
            CreateEventActionDispatcher = Dispatching("Hydrate", EventActionTriggerKind.Command, new StubAction()),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        AssertAck(SingleAck().Body, "c-1", ok: true, error: null);
    }

    [Fact]
    public async Task OnAdvancedCommand_HostHandlerOnlyAndItThrows_AcknowledgesItsError()
    {
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            OnBotEventAsync = _ => throw new InvalidOperationException("host broke"),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        AssertAck(SingleAck().Body, "c-1", ok: false, error: "host broke");
    }

    [Fact]
    public async Task OnAdvancedCommand_ThrowingOnBotEvent_DoesNotStopTheActions()
    {
        var action = new StubAction();
        var handler = CreateHandler(new CompanionTrayAppOptions
        {
            OnBotEvent = _ => throw new InvalidOperationException("ignored"),
            CreateEventActionDispatcher = Dispatching("Hydrate", EventActionTriggerKind.Command, action),
        });

        handler.OnAdvancedCommand(client, Command());
        await DrainAsync();

        Assert.Equal(1, action.Runs);
        AssertAck(SingleAck().Body, "c-1", ok: true, error: null);
    }

    [Fact]
    public async Task OnAdvancedCommand_HandsTheHostTheCommandAndItsParameters()
    {
        CreateHandler().OnAdvancedCommand(client, Command(ackRequired: false));
        await DrainAsync();

        var botEvent = Assert.Single(hostEvents);
        Assert.Equal(EventActionDispatcher.CommandEventName, botEvent.EventName);
        Assert.Equal("c-1", botEvent.Metadata["eventId"]);
        Assert.Equal("7", botEvent.Metadata["commandId"]);
        Assert.Equal("Hydrate", botEvent.Metadata["commandName"]);
        Assert.Equal("now", botEvent.Metadata["args"]);
        Assert.Equal("100", botEvent.Metadata["pointsSpent"]);
        Assert.Equal("red", botEvent.Metadata["param.color"]);
        // No display name, so the balloon falls back to the login.
        Assert.Equal([("Hydrate", "Used by viewer")], balloons);
    }

    [Fact]
    public async Task OnAdvancedCommand_FromStreamDeck_SaysSo()
    {
        CreateHandler().OnAdvancedCommand(client, Command(ackRequired: false, source: "streamdeck"));
        await DrainAsync();

        Assert.Equal([("Hydrate", "Triggered from Stream Deck")], balloons);
    }

    [Fact]
    public async Task OnAdvancedCommand_AckRejectedByTheBot_IsIgnored()
    {
        var failing = new RecordingHttpHandler(System.Net.HttpStatusCode.InternalServerError, """{"ok":false,"error":"boom"}""");
        using var failingClient = new CompanionClient(new Uri("https://bot.example.com"), new MemoryTokenStore("token"), new HttpClient(failing));

        CreateHandler().OnAdvancedCommand(failingClient, Command());
        await DrainAsync();

        Assert.Single(failing.Requests);
    }

    [Theory]
    [InlineData("follow", null, "New Follower", "Fan")]
    [InlineData("raid", "42 viewers", "Raid", "Fan — 42 viewers")]
    [InlineData("giftsub", "5 subs", "Gift Sub", "Fan — 5 subs")]
    [InlineData("hypetrain", null, "hypetrain", "Fan")] // an activity type this app doesn't know yet
    public async Task OnActivity_ShowsATitledBalloonAndHandsTheHostTheEvent(string type, string? detail, string title, string text)
    {
        CreateHandler().OnActivity(new CompanionActivityEvent { Type = type, Id = 1, DisplayName = "Fan", Detail = detail });
        await DrainAsync();

        Assert.Equal([(title, text)], balloons);
        var botEvent = Assert.Single(hostEvents);
        Assert.Equal("activity.received", botEvent.EventName);
        Assert.Equal(type, botEvent.Metadata["type"]);
        Assert.Equal(detail, botEvent.Metadata["detail"]);
    }
}
