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

    /// <summary>
    /// Stable identifier (the bot's <c>streamer_event_log.id</c>), unique across both this
    /// live SSE push and the <c>/api/companion/events/recent</c> backfill response — lets
    /// <see cref="Events.CompanionEventStream"/> dedupe/order events exactly instead of by an
    /// <see cref="OccurredAt"/>-based heuristic.
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; }
}
