using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public sealed class EventActionContextTests
{
    private sealed class ServiceA;

    private sealed class ServiceB;

    private sealed class SingleServiceContext(object service) : IEventActionContext
    {
        public object? GetService(Type serviceType) => serviceType == service.GetType() ? service : null;
    }

    [Fact]
    public void Composite_ReturnsTheFirstContextThatProvidesTheService()
    {
        var a1 = new ServiceA();
        var a2 = new ServiceA();
        var b = new ServiceB();
        var composite = new CompositeEventActionContext(
            new SingleServiceContext(a1), new SingleServiceContext(b), new SingleServiceContext(a2));

        Assert.Same(a1, composite.GetService<ServiceA>());
        Assert.Same(b, composite.GetService<ServiceB>());
        Assert.Null(composite.GetService<string>());
    }

    [Fact]
    public void Composite_WithNoContexts_ProvidesNothing()
    {
        Assert.Null(new CompositeEventActionContext().GetService(typeof(ServiceA)));
    }

    [Fact]
    public void BotEventContext_ProvidesTheEventAheadOfTheInnerContext()
    {
        var botEvent = new BotEventArgs("command.triggered", new Dictionary<string, string?> { ["param.color"] = "red" });
        var inner = new SingleServiceContext(new BotEventArgs("other", new Dictionary<string, string?>()));
        var service = new ServiceA();
        var context = new BotEventActionContext(botEvent, new CompositeEventActionContext(inner, new SingleServiceContext(service)));

        Assert.Same(botEvent, context.GetService<BotEventArgs>());
        Assert.Same(service, context.GetService<ServiceA>());
        Assert.Null(context.GetService<ServiceB>());
    }
}
