using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Polymorphic (de)serializer for <see cref="IEventAction"/>. The discriminator is a
/// hand-written "kind" property (lowercase, independent of whatever <see cref="JsonSerializerOptions"/>
/// the host configures) resolved against an <see cref="EventActionTypeRegistry"/>, since Core
/// cannot declare compile-time <c>[JsonDerivedType]</c> attributes for app-specific action types.
/// An action entry that can't be read as a registered kind (not an object, no or an unknown
/// kind, or a value the action type rejects) becomes an <see cref="UnrecognizedEventAction"/>
/// instead of failing the whole document.
/// </summary>
public sealed class EventActionJsonConverter(EventActionTypeRegistry registry) : JsonConverter<IEventAction>
{
    private const string KindPropertyName = "kind";

    /// <summary>
    /// True, so a JSON null entry reaches <see cref="Read"/> and becomes an <see cref="UnrecognizedEventAction"/>
    /// instead of leaving a null action in the mapping.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>Reads one action, turning anything that can't be read as a registered kind into an <see cref="UnrecognizedEventAction"/>.</summary>
    public override IEventAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new UnrecognizedEventAction(string.Empty, "null", "This action is empty (null), so it can't be run.");
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var json = root.GetRawText();

        return TryResolveType(root, out string kind, out Type? actionType, out string? problem)
            ? Deserialize(json, kind, actionType, options)
            : new UnrecognizedEventAction(kind, json, problem);
    }

    /// <summary>Finds the registered type for an action's "kind", or says why it can't be run.</summary>
    /// <param name="root">The action's JSON.</param>
    /// <param name="kind">The action's kind, or empty if it has none.</param>
    /// <param name="actionType">The registered type for <paramref name="kind"/>, when found.</param>
    /// <param name="problem">Why the action can't be run, when no type was found.</param>
    private bool TryResolveType(
        JsonElement root,
        out string kind,
        [NotNullWhen(true)] out Type? actionType,
        [NotNullWhen(false)] out string? problem)
    {
        kind = string.Empty;
        actionType = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = $"This action isn't a JSON object ({root.ValueKind}), so it can't be run.";
            return false;
        }

        if (!root.TryGetProperty(KindPropertyName, out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { Length: > 0 } foundKind)
        {
            problem = $"This action has no \"{KindPropertyName}\", so it can't be run.";
            return false;
        }

        kind = foundKind;
        problem = registry.TryGetType(kind, out actionType)
            ? null
            : $"Unknown action kind \"{kind}\" — this version of the app can't run it.";
        return problem is null;
    }

    /// <summary>Deserializes an action of a registered type, or an <see cref="UnrecognizedEventAction"/> if its values can't be read.</summary>
    private static IEventAction Deserialize(string json, string kind, Type actionType, JsonSerializerOptions options)
    {
        try
        {
            return (IEventAction?)JsonSerializer.Deserialize(json, actionType, options)
                ?? new UnrecognizedEventAction(kind, json, $"Couldn't read this \"{kind}\" action.");
        }
        catch (Exception ex)
        {
            // Not only JsonException: an action type's constructor or setter may reject a value
            // (e.g. ArgumentOutOfRangeException), and the serializer throws NotSupportedException or
            // InvalidOperationException for shapes it can't bind. Any of them would otherwise fail
            // the whole config, which the store then replaces with an empty one.
            return new UnrecognizedEventAction(kind, json, $"Couldn't read this \"{kind}\" action: {ex.Message}");
        }
    }

    /// <summary>Writes an action with its "kind" first, or an <see cref="UnrecognizedEventAction"/> exactly as it was read.</summary>
    public override void Write(Utf8JsonWriter writer, IEventAction? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            // HandleNull makes the serializer hand nulls to this converter on write too.
            writer.WriteNullValue();
            return;
        }

        if (value is UnrecognizedEventAction unrecognized)
        {
            // Written back exactly as read, so saving a config never loses an action this build can't run.
            using var saved = JsonDocument.Parse(unrecognized.RawJson);
            saved.RootElement.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString(KindPropertyName, value.Kind);

        using var doc = JsonSerializer.SerializeToDocument(value, value.GetType(), options);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, KindPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
