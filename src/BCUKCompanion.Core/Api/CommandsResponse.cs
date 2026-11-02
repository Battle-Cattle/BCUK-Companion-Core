using System.Text.Json.Serialization;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of GET /api/companion/commands.</summary>
internal sealed class CommandsResponse : IApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether the request succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets the streamer's advanced commands.</summary>
    [JsonPropertyName("commands")]
    public List<AdvancedCommand>? Commands { get; set; }

    /// <summary>Gets or sets the bot's error message, when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
