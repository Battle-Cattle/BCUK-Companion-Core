using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public class AdvancedCommandMappingTests
{
    private static BotEventArgs CommandEvent(string name, Dictionary<string, string?>? extra = null)
    {
        var metadata = new Dictionary<string, string?> { ["commandName"] = name };
        foreach (var (key, value) in extra ?? [])
        {
            metadata[key] = value;
        }

        return new BotEventArgs(EventActionDispatcher.CommandEventName, metadata);
    }

    [Fact]
    public async Task DispatchAsync_MatchesCommandMappingsByCommandNameOnly()
    {
        var commandRan = false;
        var rewardRan = false;
        var dispatcher = new EventActionDispatcher(
            () =>
            [
                new EventActionMapping("Hydrate", [new FakeEventAction(onExecute: () => rewardRan = true)]),
                new EventActionMapping("hydrate", [new FakeEventAction(onExecute: () => commandRan = true)], EventActionTriggerKind.Command),
            ],
            () => new NoServiceEventActionContext());

        var result = await dispatcher.DispatchAsync(CommandEvent("HYDRATE"));

        Assert.NotNull(result);
        Assert.True(result!.AllSucceeded);
        Assert.True(commandRan);
        Assert.False(rewardRan);
    }

    [Fact]
    public async Task DispatchAsync_RedemptionsDoNotRunCommandMappings()
    {
        var commandRan = false;
        var dispatcher = new EventActionDispatcher(
            () => [new EventActionMapping("Hydrate", [new FakeEventAction(onExecute: () => commandRan = true)], EventActionTriggerKind.Command)],
            () => new NoServiceEventActionContext());

        var result = await dispatcher.DispatchAsync(new BotEventArgs(
            EventActionDispatcher.RedemptionEventName, new Dictionary<string, string?> { ["rewardTitle"] = "Hydrate" }));

        Assert.Empty(result!.ActionResults);
        Assert.False(commandRan);
    }

    [Fact]
    public async Task DispatchAsync_ReturnsNullForACommandEventWithoutAName()
    {
        var dispatcher = new EventActionDispatcher(() => [], () => new NoServiceEventActionContext());

        Assert.Null(await dispatcher.DispatchAsync(CommandEvent("")));
        Assert.Null(await dispatcher.DispatchAsync(new BotEventArgs("activity.received", new Dictionary<string, string?>())));
    }

    [Fact]
    public async Task DispatchAsync_ExposesTheEventToActions()
    {
        var action = new CapturingAction();
        var dispatcher = new EventActionDispatcher(
            () => [new EventActionMapping("Lights", [action], EventActionTriggerKind.Command)],
            () => new NoServiceEventActionContext());

        await dispatcher.DispatchAsync(CommandEvent("Lights", new() { ["param.color"] = "red" }));

        Assert.Equal("red", action.SeenEvent!.Metadata["param.color"]);
    }

    [Fact]
    public void AdvancedCommandAck_IsOkOnlyWhenEveryMappedActionSucceeded()
    {
        var ok = new EventActionResult(new FakeEventAction(), true, null);
        var failed = new EventActionResult(new FakeEventAction(), false, "Lamp offline");

        Assert.Equal((false, AdvancedCommandAck.NoMappingError), AdvancedCommandAck.FromDispatchResult(null));
        Assert.Equal((false, AdvancedCommandAck.NoMappingError), AdvancedCommandAck.FromDispatchResult(new EventDispatchResult("x", [])));
        Assert.Equal((true, null), AdvancedCommandAck.FromDispatchResult(new EventDispatchResult("x", [ok])));
        Assert.Equal((false, "Lamp offline"), AdvancedCommandAck.FromDispatchResult(new EventDispatchResult("x", [ok, failed])));
    }

    [Fact]
    public void Mapping_ToStringLabelsCommandMappings()
    {
        Assert.Equal("Command: Lights (0 action(s))", new EventActionMapping("Lights", [], EventActionTriggerKind.Command).ToString());
        Assert.Equal("Lights (0 action(s))", new EventActionMapping("Lights", []).ToString());
    }

    [Fact]
    public void Combine_SucceedsOnlyWhenEveryHandlerSucceeded()
    {
        Assert.Equal((true, (string?)null), AdvancedCommandAck.Combine((true, null), (true, null)));
        Assert.Equal((false, "host boom"), AdvancedCommandAck.Combine((false, "host boom"), (true, null)));
        Assert.Equal((false, "host boom; Lamp offline"), AdvancedCommandAck.Combine((false, "host boom"), (false, "Lamp offline")));
        Assert.Equal((false, "Failed."), AdvancedCommandAck.Combine((true, null), (false, null)));
    }

    private sealed class CapturingAction : IEventAction
    {
        public BotEventArgs? SeenEvent { get; private set; }

        public string Kind => "capture";

        public string Describe(IEventActionContext context) => "Capture";

        public IReadOnlyList<string> Validate(IEventActionContext context) => [];

        public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
        {
            SeenEvent = context.GetService<BotEventArgs>();
            return Task.FromResult(true);
        }
    }
}
