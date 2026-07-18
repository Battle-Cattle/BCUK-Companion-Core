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

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("cost")]
    public int Cost { get; set; }

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

    [JsonPropertyName("isUserInputRequired")]
    public bool IsUserInputRequired { get; set; }
}
