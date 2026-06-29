namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Minimal service locator threaded through <see cref="IEventAction"/> validation, execution,
/// and display so an action type defined outside Core (e.g. a per-app device integration) can
/// reach app-specific services without Core ever referencing those types.
/// </summary>
public interface IEventActionContext
{
    object? GetService(Type serviceType);
}

public static class EventActionContextExtensions
{
    public static T? GetService<T>(this IEventActionContext context) where T : class
        => context.GetService(typeof(T)) as T;
}
