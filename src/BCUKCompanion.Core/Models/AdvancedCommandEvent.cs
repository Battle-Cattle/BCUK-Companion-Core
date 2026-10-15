using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// One use of a streamer's advanced command, pushed over the companion SSE stream as
/// <c>type: "advanced_command"</c>. Field names mirror the JSON payload documented in
/// companionappsetupguide.md exactly. When <see cref="AckRequired"/> is set the app must report
/// back via <see cref="CompanionClient.AckAdvancedCommandAsync"/>, or the bot refunds the viewer.
/// </summary>
public sealed class AdvancedCommandEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Unique per use; quoted back in the acknowledgement.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("commandId")]
    public int CommandId { get; set; }

    /// <summary>The command's name — what command mappings match on.</summary>
    [JsonPropertyName("commandName")]
    public string CommandName { get; set; } = string.Empty;

    /// <summary><c>"chat"</c> or <c>"streamdeck"</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>The viewer's Twitch login; null for a Stream Deck use.</summary>
    [JsonPropertyName("userLogin")]
    public string? UserLogin { get; set; }

    /// <summary>The viewer's display name; null for a Stream Deck use.</summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    /// <summary>Text the viewer typed after the trigger, or null.</summary>
    [JsonPropertyName("args")]
    public string? Args { get; set; }

    [JsonPropertyName("pointsSpent")]
    public int PointsSpent { get; set; }

    /// <summary>The command's key/value parameters, set by the streamer on the bot's web panel.</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, string>? Parameters { get; set; }

    [JsonPropertyName("ackRequired")]
    public bool AckRequired { get; set; }

    [JsonPropertyName("triggeredAt")]
    public DateTimeOffset TriggeredAt { get; set; }
}
