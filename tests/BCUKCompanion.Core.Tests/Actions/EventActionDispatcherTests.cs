using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public class EventActionDispatcherTests
{
    private static EventActionDispatcher CreateDispatcher(IReadOnlyList<EventActionMapping> mappings)
        => new(() => mappings, () => new NoServiceEventActionContext());

    [Fact]
    public async Task DispatchAsync_MatchesRewardTitleCaseInsensitively()
    {
        var executed = false;
        var mapping = new EventActionMapping("Hydrate!", [new FakeEventAction(onExecute: () => executed = true)]);
        var dispatcher = CreateDispatcher([mapping]);

        var result = await dispatcher.DispatchAsync("HYDRATE!");

        Assert.True(executed);
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task DispatchAsync_NoMatchingMapping_ReturnsEmptyResults()
    {
        var mapping = new EventActionMapping("Hydrate!", [new FakeEventAction()]);
        var dispatcher = CreateDispatcher([mapping]);

        var result = await dispatcher.DispatchAsync("Something Else");

        Assert.Empty(result.ActionResults);
        Assert.False(result.AllSucceeded);
    }

    [Fact]
    public async Task DispatchAsync_ExecutesActionsSequentiallyInMappingOrder()
    {
        var order = new List<int>();
        var mappingA = new EventActionMapping("Hydrate!", [new FakeEventAction(onExecute: () => order.Add(1))]);
        var mappingB = new EventActionMapping("Hydrate!", [new FakeEventAction(onExecute: () => order.Add(2))]);
        var dispatcher = CreateDispatcher([mappingA, mappingB]);

        await dispatcher.DispatchAsync("Hydrate!");

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task DispatchAsync_DelayBlocksSubsequentActions()
    {
        DateTime? executedAt = null;
        var mapping = new EventActionMapping(
            "Hydrate!",
            [new DelayAction { DelaySeconds = 1 }, new FakeEventAction(onExecute: () => executedAt = DateTime.UtcNow)]);
        var dispatcher = CreateDispatcher([mapping]);

        var start = DateTime.UtcNow;
        await dispatcher.DispatchAsync("Hydrate!");

        Assert.NotNull(executedAt);
        Assert.True(executedAt!.Value - start >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task DispatchAsync_ValidationFailure_RecordsErrorWithoutExecuting()
    {
        var executed = false;
        var mapping = new EventActionMapping(
            "Hydrate!",
            [new FakeEventAction(validationErrors: ["bad"], onExecute: () => executed = true)]);
        var dispatcher = CreateDispatcher([mapping]);

        var result = await dispatcher.DispatchAsync("Hydrate!");

        Assert.False(executed);
        Assert.False(result.ActionResults[0].Success);
        Assert.Equal("bad", result.ActionResults[0].ErrorMessage);
    }

    [Fact]
    public async Task DispatchAsync_ExceptionDuringExecute_CaughtAndRecordedAsFailure()
    {
        var mapping = new EventActionMapping(
            "Hydrate!",
            [new FakeEventAction(throwOnExecute: new InvalidOperationException("boom"))]);
        var dispatcher = CreateDispatcher([mapping]);

        var result = await dispatcher.DispatchAsync("Hydrate!");

        Assert.False(result.ActionResults[0].Success);
        Assert.Equal("boom", result.ActionResults[0].ErrorMessage);
    }

    [Fact]
    public async Task DispatchAsync_BotEventArgs_IgnoresNonRedemptionEvents()
    {
        var dispatcher = CreateDispatcher([new EventActionMapping("Hydrate!", [new FakeEventAction()])]);

        var result = await dispatcher.DispatchAsync(new BotEventArgs("some.other.event", new Dictionary<string, string?>
        {
            ["rewardTitle"] = "Hydrate!",
        }));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DispatchAsync_BotEventArgs_IgnoresMissingOrEmptyRewardTitle(string? rewardTitle)
    {
        var dispatcher = CreateDispatcher([new EventActionMapping("Hydrate!", [new FakeEventAction()])]);
        var metadata = new Dictionary<string, string?>();
        if (rewardTitle is not null)
        {
            metadata["rewardTitle"] = rewardTitle;
        }

        var result = await dispatcher.DispatchAsync(new BotEventArgs("redemption.received", metadata));

        Assert.Null(result);
    }

    [Fact]
    public async Task DispatchAsync_BotEventArgs_DelegatesToStringOverload()
    {
        var executed = false;
        var mapping = new EventActionMapping("Hydrate!", [new FakeEventAction(onExecute: () => executed = true)]);
        var dispatcher = CreateDispatcher([mapping]);

        var result = await dispatcher.DispatchAsync(new BotEventArgs("redemption.received", new Dictionary<string, string?>
        {
            ["rewardTitle"] = "Hydrate!",
        }));

        Assert.True(executed);
        Assert.NotNull(result);
        Assert.Equal("Hydrate!", result!.RewardTitle);
    }
}
