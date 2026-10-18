using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// A single Twitch channel-point redemption pushed over the companion SSE
/// stream. Field names mirror the JSON payload documented in
/// companionappsetupguide.md exactly. When <see cref="AckRequired"/> is set the app must report
/// back via <see cref="CompanionClient.AckRedemptionAsync"/>, or the bot cancels the redemption
/// and refunds the viewer.
/// </summary>
public sealed class RedemptionEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Twitch's redemption id; quoted back in the acknowledgement. Empty from bots that predate
    /// companion confirmation.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

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

    /// <summary>
    /// Set when the reward requires companion confirmation: the bot refunds the viewer unless
    /// this app acknowledges that every mapped action succeeded.
    /// </summary>
    [JsonPropertyName("ackRequired")]
    public bool AckRequired { get; set; }

    [JsonPropertyName("redeemedAt")]
    public DateTimeOffset RedeemedAt { get; set; }

    /// <summary>The viewer's display name, falling back to their login when it's empty.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(UserName) ? UserLogin : UserName;

    /// <summary>
    /// Whether this app must acknowledge the redemption: the bot asked for it, and sent an
    /// <see cref="Id"/> to acknowledge it by.
    /// </summary>
    [JsonIgnore]
    public bool NeedsAck => AckRequired && !string.IsNullOrEmpty(Id);
}
