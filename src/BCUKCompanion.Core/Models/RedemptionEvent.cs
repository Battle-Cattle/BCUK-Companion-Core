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
    /// <summary>The event type: always <c>"channel_points_redemption"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Twitch's redemption id; quoted back in the acknowledgement. Empty from bots that predate
    /// companion confirmation.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The redeemed reward's Twitch ID (matches <see cref="Reward.Id"/>).</summary>
    [JsonPropertyName("rewardId")]
    public string RewardId { get; set; } = string.Empty;

    /// <summary>The redeemed reward's title: what reward mappings match on.</summary>
    [JsonPropertyName("rewardTitle")]
    public string RewardTitle { get; set; } = string.Empty;

    /// <summary>The viewer's Twitch login.</summary>
    [JsonPropertyName("userLogin")]
    public string UserLogin { get; set; } = string.Empty;

    /// <summary>The viewer's display name.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Text the viewer entered, for a reward that asks for it, or null.</summary>
    [JsonPropertyName("userInput")]
    public string? UserInput { get; set; }

    /// <summary>
    /// Set when the reward requires companion confirmation: the bot refunds the viewer unless
    /// this app acknowledges that every mapped action succeeded.
    /// </summary>
    [JsonPropertyName("ackRequired")]
    public bool AckRequired { get; set; }

    /// <summary>When the reward was redeemed.</summary>
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
