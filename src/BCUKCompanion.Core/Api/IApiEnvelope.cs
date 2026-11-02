namespace BCUKCompanion.Core.Api;

/// <summary>Common envelope shape (<c>{ ok, error, ... }</c>) every companion request/response API returns.</summary>
internal interface IApiEnvelope
{
    /// <summary>Gets a value indicating whether the request succeeded.</summary>
    bool Ok { get; }

    /// <summary>Gets the bot's error message, when <see cref="Ok"/> is false.</summary>
    string? Error { get; }
}
