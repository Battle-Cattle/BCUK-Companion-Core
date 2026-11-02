using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Api;

/// <summary>
/// Common envelope shape (<c>{ ok, error, ... }</c>) every companion request/response API returns.
/// Each response type derives from it and adds only its own payload.
/// </summary>
internal abstract class ApiEnvelope
{
    /// <summary>Gets or sets a value indicating whether the request succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets the bot's error message, when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
