using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// One of the streamer's advanced commands, as returned by GET /api/companion/commands. Field
/// names mirror the JSON payload documented in companionappsetupguide.md exactly.
/// </summary>
public sealed class AdvancedCommand
{
    /// <summary>The command's ID on the bot.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Matches <see cref="AdvancedCommandEvent.CommandName"/>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The chat trigger (e.g. <c>!hydrate</c>), or null for a Stream Deck-only command.</summary>
    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }

    /// <summary>What a use costs the viewer, in channel points.</summary>
    [JsonPropertyName("pointCost")]
    public int PointCost { get; set; }

    /// <summary>Whether the command can currently be used.</summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    /// <summary>Whether uses are sent to the companion app at all.</summary>
    [JsonPropertyName("companionEnabled")]
    public bool CompanionEnabled { get; set; }

    /// <summary>The command's key/value parameters, set by the streamer on the bot's web panel.</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, string>? Parameters { get; set; }
}
