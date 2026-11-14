using BCUKCompanion.Core.Events;
using Xunit;

namespace BCUKCompanion.Core.Tests.Events;

public sealed class SubscriberInvokerTests
{
    [Fact]
    public void Raise_WithNoSubscribers_DoesNothing()
    {
        SubscriberInvoker.Raise<int>(null, this, 1, _ => throw new InvalidOperationException("not expected"));
    }

    [Fact]
    public void Raise_RunsEverySubscriberAndReportsEachFailure()
    {
        var calls = new List<string>();
        var failures = new List<string>();
        EventHandler<int>? handler = null;
        handler += (_, value) => calls.Add($"first {value}");
        handler += (_, _) => throw new InvalidOperationException("boom");
        handler += (_, value) => calls.Add($"third {value}");

        SubscriberInvoker.Raise(handler, this, 7, ex => failures.Add(ex.Message));

        Assert.Equal(["first 7", "third 7"], calls);
        Assert.Equal(["boom"], failures);
    }

    [Fact]
    public void Raise_SwallowsAFailureCallbackThatThrows()
    {
        var calls = 0;
        EventHandler<int>? handler = null;
        handler += (_, _) => throw new InvalidOperationException("boom");
        handler += (_, _) => calls++;

        SubscriberInvoker.Raise(handler, this, 1, _ => throw new InvalidOperationException("callback"));

        Assert.Equal(1, calls);
    }
}
