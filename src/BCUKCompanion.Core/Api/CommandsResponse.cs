using System.Text.Json.Serialization;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of GET /api/companion/commands.</summary>
internal sealed class CommandsResponse : ApiEnvelope
{
    /// <summary>Gets or sets the streamer's advanced commands.</summary>
    [JsonPropertyName("commands")]
    public List<AdvancedCommand>? Commands { get; set; }
}
