using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of the command and redemption ack routes.</summary>
internal sealed class AckResponse : IApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether the request succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets a value indicating whether this acknowledgement resolved the event (false if the bot already had, e.g. on timeout).</summary>
    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }

    /// <summary>Gets or sets the bot's error message, when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
