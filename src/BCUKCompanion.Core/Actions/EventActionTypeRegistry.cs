namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Maps the JSON "kind" discriminator string to a concrete <see cref="IEventAction"/> type, so
/// <see cref="EventActionJsonConverter"/> can deserialize polymorphic action lists without Core
/// referencing app-specific types. Apps register their action kinds once at startup.
/// </summary>
public sealed class EventActionTypeRegistry
{
    private readonly Dictionary<string, Type> kindToType = new(StringComparer.Ordinal);

    public EventActionTypeRegistry()
    {
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

        if (kindToType.ContainsKey(kind))
        {
            throw new ArgumentException($"Action kind \"{kind}\" is already registered.", nameof(kind));
        }

        if (!actionType.IsClass || actionType.IsAbstract || !typeof(IEventAction).IsAssignableFrom(actionType))
        {
            throw new ArgumentException($"{actionType} must be a concrete class that implements {nameof(IEventAction)}.", nameof(actionType));
        }

        kindToType[kind] = actionType;
    }

    public bool TryGetType(string kind, out Type actionType) => kindToType.TryGetValue(kind, out actionType!);

    public IReadOnlyDictionary<string, Type> RegisteredKinds => kindToType.AsReadOnly();
}
