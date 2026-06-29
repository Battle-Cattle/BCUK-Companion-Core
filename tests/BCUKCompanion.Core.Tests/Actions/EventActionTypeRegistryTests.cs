using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

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
    public void Register_OverwritesExistingKind()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register("delay", typeof(FakeEventAction));

        Assert.True(registry.TryGetType("delay", out var type));
        Assert.Equal(typeof(FakeEventAction), type);
    }

    [Fact]
    public void Register_ThrowsForNonIEventActionType()
    {
        var registry = new EventActionTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("bad", typeof(string)));
    }
}
