using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

internal abstract class AbstractEventAction : IEventAction
{
    public abstract string Kind { get; }
    public abstract string Describe(IEventActionContext context);
    public abstract IReadOnlyList<string> Validate(IEventActionContext context);
    public abstract Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken);
}

public class EventActionTypeRegistryTests
{
    [Fact]
    public void Constructor_PreRegistersDelayAction()
    {
        var registry = new EventActionTypeRegistry();

        Assert.True(registry.TryGetType("delay", out var type));
        Assert.Equal(typeof(DelayAction), type);
    }

    [Fact]
    public void Register_AddsCustomKind()
    {
        var registry = new EventActionTypeRegistry();

        registry.Register<FakeEventAction>("fake");

        Assert.True(registry.TryGetType("fake", out var type));
        Assert.Equal(typeof(FakeEventAction), type);
    }

    [Fact]
    public void Register_ThrowsForDuplicateKind()
    {
        var registry = new EventActionTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("delay", typeof(FakeEventAction)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_ThrowsForNullOrWhitespaceKind(string? kind)
    {
        var registry = new EventActionTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(kind!, typeof(FakeEventAction)));
    }

    [Fact]
    public void Register_ThrowsForNonIEventActionType()
    {
        var registry = new EventActionTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("bad", typeof(string)));
    }

    [Fact]
    public void Register_ThrowsForInterfaceType()
    {
        var registry = new EventActionTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("bad", typeof(IEventAction)));
    }

    [Fact]
    public void Register_ThrowsForAbstractType()
    {
        var registry = new EventActionTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("bad", typeof(AbstractEventAction)));
    }
}
