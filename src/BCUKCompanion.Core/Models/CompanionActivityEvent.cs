using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Models;

/// <summary>
/// A streamer activity event (follow/sub/resub/giftsub/raid) pushed over the
/// companion SSE stream, or returned by the GET /api/companion/events/recent
/// backfill endpoint. Field names mirror the JSON payload documented in
/// companionappsetupguide.md exactly. Distinct from <see cref="RedemptionEvent"/>,
/// which covers channel-point redemptions only.
/// </summary>
public sealed class CompanionActivityEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; }
}
