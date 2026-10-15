using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// One of the streamer's advanced commands, as returned by GET /api/companion/commands. Field
/// names mirror the JSON payload documented in companionappsetupguide.md exactly.
/// </summary>
public sealed class AdvancedCommand
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Matches <see cref="AdvancedCommandEvent.CommandName"/>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The chat trigger (e.g. <c>!hydrate</c>), or null for a Stream Deck-only command.</summary>
    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }

    [JsonPropertyName("pointCost")]
    public int PointCost { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    /// <summary>Whether uses are sent to the companion app at all.</summary>
    [JsonPropertyName("companionEnabled")]
    public bool CompanionEnabled { get; set; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, string>? Parameters { get; set; }
}
