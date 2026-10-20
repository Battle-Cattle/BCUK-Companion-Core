namespace BCUKCompanion.Core.Actions;

/// <summary>
/// A single action a companion app executes in response to a bot event. Core ships
/// <see cref="DelayAction"/>; apps contribute their own kinds by implementing this interface
/// and registering a type with <see cref="EventActionTypeRegistry"/>.
/// </summary>
public interface IEventAction
{
    /// <summary>
    /// Stable, JSON-persisted discriminator for this action's concrete type (e.g. "delay").
    /// Must match the key it was registered under in <see cref="EventActionTypeRegistry"/>.
    /// </summary>
    string Kind { get; }

    /// <summary>Human-readable summary for UI display (mapping/action lists), e.g. "Delay 5s".</summary>
    string Describe(IEventActionContext context);

    /// <summary>Returns validation error messages; an empty list means valid.</summary>
    IReadOnlyList<string> Validate(IEventActionContext context);

    /// <summary>Runs the action. The dispatcher validates it first and turns an exception into a failed result, so an implementation can throw for a missing service or device.</summary>
    /// <param name="context">
    /// Provides the services the action needs. When dispatched for a bot event, it also provides that
    /// event as <c>BotEventArgs</c>; a run without one (such as the mappings window's Test button) has none.
    /// </param>
    /// <param name="cancellationToken">Cancels the action, e.g. when the app is shutting down.</param>
    /// <returns>Whether the action succeeded.</returns>
    Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken);
}
