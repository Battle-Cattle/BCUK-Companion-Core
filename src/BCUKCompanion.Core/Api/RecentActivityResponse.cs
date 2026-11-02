using System.Text.Json.Serialization;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of GET /api/companion/events/recent.</summary>
internal sealed class RecentActivityResponse : ApiEnvelope
{
    /// <summary>Gets or sets the missed activity events.</summary>
    [JsonPropertyName("events")]
    public List<CompanionActivityEvent>? Events { get; set; }
}
