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
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new UnrecognizedEventAction(string.Empty, json, $"This action isn't a JSON object ({root.ValueKind}), so it can't be run.");
        }

        if (!root.TryGetProperty(KindPropertyName, out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { Length: > 0 } kind)
        {
            return new UnrecognizedEventAction(string.Empty, json, $"This action has no \"{KindPropertyName}\", so it can't be run.");
        }

        if (!registry.TryGetType(kind, out var actionType))
        {
            return new UnrecognizedEventAction(kind, json, $"Unknown action kind \"{kind}\" — this version of the app can't run it.");
        }

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
