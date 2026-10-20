namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Minimal service locator threaded through <see cref="IEventAction"/> validation, execution,
/// and display so an action type defined outside Core (e.g. a per-app device integration) can
/// reach app-specific services without Core ever referencing those types.
/// </summary>
public interface IEventActionContext
{
    /// <summary>Returns the service of <paramref name="serviceType"/>, or null if this context doesn't provide one.</summary>
    /// <param name="serviceType">The type of service an action is asking for.</param>
    object? GetService(Type serviceType);
}

/// <summary>Typed helpers over <see cref="IEventActionContext"/>.</summary>
public static class EventActionContextExtensions
{
    /// <summary>Returns the context's <typeparamref name="T"/> service, or null if it doesn't provide one.</summary>
    /// <typeparam name="T">The type of service to look up.</typeparam>
    /// <param name="context">The context to ask.</param>
    public static T? GetService<T>(this IEventActionContext context) where T : class
        => context.GetService(typeof(T)) as T;
}
