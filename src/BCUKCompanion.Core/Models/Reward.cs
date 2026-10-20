using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// A Twitch channel-point custom reward, as returned by
/// GET /api/companion/rewards. Field names mirror the JSON payload
/// documented in companionappsetupguide.md exactly.
/// </summary>
public sealed class Reward
{
    /// <summary>The Twitch reward UUID — matches <see cref="RedemptionEvent.RewardId"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The reward's title, as viewers see it (matches <see cref="RedemptionEvent.RewardTitle"/>).</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The reward's description shown to viewers.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>What the reward costs, in channel points.</summary>
    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    /// <summary>The reward's background colour on Twitch, as a hex string (e.g. <c>#00E5CB</c>).</summary>
    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>
    /// Whether the reward is currently active on Twitch. The server does not filter
    /// disabled rewards out of the response — a paused reward can still appear here
    /// with this set to false, so callers that only want to show active rewards must
    /// filter on this themselves.
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    /// <summary>Whether viewers must enter text to redeem it (sent as <see cref="RedemptionEvent.UserInput"/>).</summary>
    [JsonPropertyName("isUserInputRequired")]
    public bool IsUserInputRequired { get; set; }

    /// <summary>
    /// Whether the reward requires companion confirmation: its redemptions arrive with
    /// <see cref="RedemptionEvent.AckRequired"/> set, and are refunded unless acknowledged.
    /// </summary>
    [JsonPropertyName("companionConfirmationRequired")]
    public bool CompanionConfirmationRequired { get; set; }
}
