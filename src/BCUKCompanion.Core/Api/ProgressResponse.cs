using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of the command and redemption progress routes.</summary>
internal sealed class ProgressResponse : ApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether the bot restarted its acknowledgement timeout.</summary>
    [JsonPropertyName("extended")]
    public bool Extended { get; set; }
}
