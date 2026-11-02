using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of the command and redemption ack routes.</summary>
internal sealed class AckResponse : ApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether this acknowledgement resolved the event (false if the bot already had, e.g. on timeout).</summary>
    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }
}
