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

    Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken);
}
