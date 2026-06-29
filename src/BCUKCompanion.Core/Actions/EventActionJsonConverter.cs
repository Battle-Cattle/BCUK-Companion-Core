using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Polymorphic (de)serializer for <see cref="IEventAction"/>. The discriminator is a
/// hand-written "kind" property (lowercase, independent of whatever <see cref="JsonSerializerOptions"/>
/// the host configures) resolved against an <see cref="EventActionTypeRegistry"/>, since Core
/// cannot declare compile-time <c>[JsonDerivedType]</c> attributes for app-specific action types.
/// </summary>
public sealed class EventActionJsonConverter(EventActionTypeRegistry registry) : JsonConverter<IEventAction>
{
    private const string KindPropertyName = "kind";

    public override IEventAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty(KindPropertyName, out var kindElement) || kindElement.GetString() is not { } kind)
        {
            throw new JsonException($"Event action JSON is missing a \"{KindPropertyName}\" discriminator.");
        }

        if (!registry.TryGetType(kind, out var actionType))
        {
            throw new JsonException($"Unknown event action kind \"{kind}\".");
        }

        var json = root.GetRawText();
        return (IEventAction?)JsonSerializer.Deserialize(json, actionType, options)
            ?? throw new JsonException($"Failed to deserialize event action of kind \"{kind}\".");
    }

    public override void Write(Utf8JsonWriter writer, IEventAction value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(KindPropertyName, value.Kind);

        using var doc = JsonSerializer.SerializeToDocument(value, value.GetType(), options);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
