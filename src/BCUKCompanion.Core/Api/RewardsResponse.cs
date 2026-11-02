using System.Text.Json.Serialization;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of GET /api/companion/rewards.</summary>
internal sealed class RewardsResponse : ApiEnvelope
{
    /// <summary>Gets or sets the channel's custom rewards.</summary>
    [JsonPropertyName("rewards")]
    public List<Reward>? Rewards { get; set; }
}
