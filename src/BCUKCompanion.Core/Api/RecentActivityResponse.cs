using System.Text.Json.Serialization;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of GET /api/companion/events/recent.</summary>
internal sealed class RecentActivityResponse : IApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether the request succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets the missed activity events.</summary>
    [JsonPropertyName("events")]
    public List<CompanionActivityEvent>? Events { get; set; }

    /// <summary>Gets or sets the bot's error message, when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
