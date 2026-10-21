using System.Text.Json;

namespace BCUKCompanion.Core;

/// <summary>Small JSON helpers shared by API response parsing and SSE payload dispatch.</summary>
internal static class JsonHelpers
{
    /// <summary>
    /// Reads a single top-level string property from a JSON object. Returns null if the input
    /// isn't valid JSON, isn't an object, the property is missing, or it isn't a string.
    /// </summary>
    /// <param name="json">The JSON text to parse.</param>
    /// <param name="propertyName">The top-level property to read, matched case-sensitively.</param>
    /// <returns>The property's string value, or null when it can't be read as one.</returns>
    public static string? TryGetString(string json, string propertyName)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out JsonElement element)
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON, or malformed -- callers treat this the same as "absent".
        }

        return null;
    }
}
