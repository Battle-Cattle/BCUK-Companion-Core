using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public sealed class EventDispatchResultTests
{
    private static readonly IEventAction Action = new DelayAction { DelaySeconds = 1 };

    [Fact]
    public void FailureSummary_NoActions_IsNull()
    {
        Assert.Null(new EventDispatchResult("Hydrate!", []).FailureSummary);
    }

    [Fact]
    public void FailureSummary_AllSucceeded_IsNull()
    {
        Assert.Null(new EventDispatchResult("Hydrate!", [new EventActionResult(Action, true, null)]).FailureSummary);
    }

    [Fact]
    public void FailureSummary_JoinsFailedActionErrors()
    {
        var result = new EventDispatchResult("Hydrate!",
        [
            new EventActionResult(Action, false, "Device offline."),
            new EventActionResult(Action, true, null),
            new EventActionResult(Action, false, null),
        ]);

        Assert.Equal("Device offline.; Action failed.", result.FailureSummary);
    }
}
