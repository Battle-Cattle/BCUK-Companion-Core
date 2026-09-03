using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Maps the JSON "kind" discriminator string to a concrete <see cref="IEventAction"/> type, so
/// <see cref="EventActionJsonConverter"/> can deserialize polymorphic action lists without Core
/// referencing app-specific types. Apps register their action kinds once at startup.
/// </summary>
/// <remarks>
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>, so <see cref="Register(string, Type)"/>
/// and <see cref="TryGetType"/> are safe to call concurrently from multiple threads (e.g. a host
/// performing async init across several callbacks). Concurrent <see cref="Register(string, Type)"/>
/// calls for the same kind still resolve deterministically: exactly one caller succeeds, and the
/// rest throw <see cref="ArgumentException"/>.
/// </remarks>
public sealed class EventActionTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> kindToType = new(StringComparer.Ordinal);
    private readonly ReadOnlyDictionary<string, Type> registeredKinds;

    public EventActionTypeRegistry()
    {
        registeredKinds = new ReadOnlyDictionary<string, Type>(kindToType);
        Register(DelayAction.ActionKind, typeof(DelayAction));
    }

    public void Register<TAction>(string kind) where TAction : IEventAction
        => Register(kind, typeof(TAction));

    public void Register(string kind, Type actionType)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Action kind must not be null or whitespace.", nameof(kind));
        }

        if (!actionType.IsClass || actionType.IsAbstract || !typeof(IEventAction).IsAssignableFrom(actionType))
        {
            throw new ArgumentException($"{actionType} must be a concrete class that implements {nameof(IEventAction)}.", nameof(actionType));
        }

        if (!kindToType.TryAdd(kind, actionType))
        {
            throw new ArgumentException($"Action kind \"{kind}\" is already registered.", nameof(kind));
        }
    }

    public bool TryGetType(string kind, out Type actionType) => kindToType.TryGetValue(kind, out actionType!);

    public IReadOnlyDictionary<string, Type> RegisteredKinds => registeredKinds;
}
