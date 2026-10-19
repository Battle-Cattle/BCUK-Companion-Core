using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Polymorphic (de)serializer for <see cref="IEventAction"/>. The discriminator is a
/// hand-written "kind" property (lowercase, independent of whatever <see cref="JsonSerializerOptions"/>
/// the host configures) resolved against an <see cref="EventActionTypeRegistry"/>, since Core
/// cannot declare compile-time <c>[JsonDerivedType]</c> attributes for app-specific action types.
/// An action object that can't be read as a registered kind becomes an
/// <see cref="UnrecognizedEventAction"/> instead of failing the whole document.
/// </summary>
public sealed class EventActionJsonConverter(EventActionTypeRegistry registry) : JsonConverter<IEventAction>
{
    private const string KindPropertyName = "kind";

    public override IEventAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Event action JSON must be a JSON object, got {root.ValueKind}.");
        }

        var json = root.GetRawText();
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
        catch (JsonException ex)
        {
            return new UnrecognizedEventAction(kind, json, $"Couldn't read this \"{kind}\" action: {ex.Message}");
        }
    }

    public override void Write(Utf8JsonWriter writer, IEventAction value, JsonSerializerOptions options)
    {
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
