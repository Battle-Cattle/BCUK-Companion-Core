using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace BCUKCompanion.Core.Events;

/// <summary>Parses the event stream's JSON payloads, treating anything unreadable as "drop it".</summary>
internal static class EventJson
{
    /// <summary>
    /// Deserializes <paramref name="json"/> as <typeparamref name="T"/>. Malformed JSON and a JSON
    /// <c>null</c> both fail, so a caller can drop the payload with one check.
    /// </summary>
    /// <typeparam name="T">The payload type to read.</typeparam>
    /// <param name="json">The payload text.</param>
    /// <param name="value">The parsed payload when this returns true; otherwise null.</param>
    /// <returns>True when <paramref name="json"/> held a non-null <typeparamref name="T"/>.</returns>
    public static bool TryDeserialize<T>(string json, [NotNullWhen(true)] out T? value)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            value = null;
        }

        return value is not null;
    }
}
