using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// A single Twitch channel-point redemption pushed over the companion SSE
/// stream. Field names mirror the JSON payload documented in
/// companionappsetupguide.md exactly.
/// </summary>
public sealed class RedemptionEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("rewardId")]
    public string RewardId { get; set; } = string.Empty;

    [JsonPropertyName("rewardTitle")]
    public string RewardTitle { get; set; } = string.Empty;

    [JsonPropertyName("userLogin")]
    public string UserLogin { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("userInput")]
    public string? UserInput { get; set; }

    [JsonPropertyName("redeemedAt")]
    public DateTimeOffset RedeemedAt { get; set; }
}
