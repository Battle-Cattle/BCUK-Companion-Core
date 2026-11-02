using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Api;

/// <summary>The response of the command and redemption progress routes.</summary>
internal sealed class ProgressResponse : IApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether the request succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets a value indicating whether the bot restarted its acknowledgement timeout.</summary>
    [JsonPropertyName("extended")]
    public bool Extended { get; set; }

    /// <summary>Gets or sets the bot's error message, when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
