namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Combines multiple <see cref="IEventActionContext"/>s so a single <see cref="EventActionMapping"/>
/// can freely mix actions from different integrations: each action's <c>GetService&lt;T&gt;()</c>
/// call resolves to whichever integration-specific context can supply it, in order, without any
/// integration's action types knowing about the others. Lives in Core so an app with more than one
/// integration doesn't have to write this chaining itself.
/// </summary>
public sealed class CompositeEventActionContext(params IEventActionContext[] contexts) : IEventActionContext
{
    public object? GetService(Type serviceType)
    {
        foreach (var context in contexts)
        {
            if (context.GetService(serviceType) is { } service)
            {
                return service;
            }
        }

        return null;
    }
}
