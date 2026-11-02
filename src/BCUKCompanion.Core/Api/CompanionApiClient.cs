using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BCUKCompanion.Core.Models;
using BCUKCompanion.Core.Tokens;

namespace BCUKCompanion.Core.Api;

/// <summary>
/// The companion API's request/response calls (rewards, recent activity, advanced commands,
/// acknowledgements, progress reports and token revocation), authenticated with the token
/// saved in the token store at the time of each call. <see cref="CompanionClient"/> exposes
/// these through its own public methods; the long-lived event stream lives elsewhere.
/// </summary>
internal sealed class CompanionApiClient
{
    /// <summary>How long <see cref="RevokeTokenAsync"/> waits for the bot to revoke the token.</summary>
    internal static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly Uri _botHost;
    private readonly ITokenStore _tokenStore;

    /// <summary>Creates an API client for the bot at <paramref name="botHost"/>.</summary>
    /// <param name="httpClient">The client every request is sent with; not disposed by this instance.</param>
    /// <param name="botHost">The bot's absolute base URL that API paths are resolved against.</param>
    /// <param name="tokenStore">Where the bearer token for each request is loaded from.</param>
    public CompanionApiClient(HttpClient httpClient, Uri botHost, ITokenStore tokenStore)
    {
        _httpClient = httpClient;
        _botHost = botHost;
        _tokenStore = tokenStore;
    }

    /// <summary>Fetches every Twitch channel-point custom reward from GET /api/companion/rewards.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is every custom reward, paused ones included; empty when the bot returns none.</returns>
    public async Task<IReadOnlyList<Reward>> GetRewardsAsync(CancellationToken cancellationToken)
    {
        RewardsResponse parsed = await FetchAsync<RewardsResponse>(
            "/api/companion/rewards", "Fetching rewards", cancellationToken).ConfigureAwait(false);

        return (IReadOnlyList<Reward>?)parsed.Rewards ?? Array.Empty<Reward>();
    }

    /// <summary>Fetches the streamer activity events missed while disconnected, from GET /api/companion/events/recent.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is the recent activity events the bot returned; empty when there are none.</returns>
    public async Task<IReadOnlyList<CompanionActivityEvent>> GetRecentActivityEventsAsync(CancellationToken cancellationToken)
    {
        RecentActivityResponse parsed = await FetchAsync<RecentActivityResponse>(
            "/api/companion/events/recent", "Fetching recent activity", cancellationToken).ConfigureAwait(false);

        return (IReadOnlyList<CompanionActivityEvent>?)parsed.Events ?? Array.Empty<CompanionActivityEvent>();
    }

    /// <summary>Fetches the streamer's advanced commands from GET /api/companion/commands.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is the streamer's advanced commands; empty when there are none or the user isn't a streamer.</returns>
    public async Task<IReadOnlyList<AdvancedCommand>> GetAdvancedCommandsAsync(CancellationToken cancellationToken)
    {
        CommandsResponse parsed = await FetchAsync<CommandsResponse>(
            "/api/companion/commands", "Fetching advanced commands", cancellationToken).ConfigureAwait(false);

        return (IReadOnlyList<AdvancedCommand>?)parsed.Commands ?? Array.Empty<AdvancedCommand>();
    }

    /// <summary>Reports the outcome of an advanced command's actions via POST /api/companion/commands/ack.</summary>
    /// <param name="eventId">The ID of the advanced command event whose actions ran.</param>
    /// <param name="ok">Whether every action succeeded.</param>
    /// <param name="error">Why not, when <paramref name="ok"/> is false.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is true if the acknowledgement resolved the use; false if the bot had already resolved it.</returns>
    public async Task<bool> AckAdvancedCommandAsync(string eventId, bool ok, string? error, CancellationToken cancellationToken)
    {
        AckResponse parsed = await PostJsonAsync<AckResponse>(
            "/api/companion/commands/ack", new { eventId, ok, error }, "Acknowledging command", cancellationToken).ConfigureAwait(false);
        return parsed.Resolved;
    }

    /// <summary>Reports the outcome of a redemption's actions via POST /api/companion/rewards/ack.</summary>
    /// <param name="redemptionId">The ID of the redemption whose actions ran.</param>
    /// <param name="ok">Whether every action succeeded.</param>
    /// <param name="error">Why not, when <paramref name="ok"/> is false.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is true if the acknowledgement resolved the redemption; false if the bot had already resolved it.</returns>
    public async Task<bool> AckRedemptionAsync(string redemptionId, bool ok, string? error, CancellationToken cancellationToken)
    {
        AckResponse parsed = await PostJsonAsync<AckResponse>(
            "/api/companion/rewards/ack", new { eventId = redemptionId, ok, error }, "Acknowledging redemption", cancellationToken).ConfigureAwait(false);
        return parsed.Resolved;
    }

    /// <summary>Tells the bot an advanced command's actions are still running, via POST /api/companion/commands/progress.</summary>
    /// <param name="eventId">The ID of the advanced command event whose actions are still running.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is true if the bot restarted its acknowledgement timeout; false if it's no longer waiting for the event.</returns>
    public Task<bool> ReportCommandProgressAsync(string eventId, CancellationToken cancellationToken)
        => ReportProgressAsync("/api/companion/commands/progress", eventId, "Reporting command progress", cancellationToken);

    /// <summary>Tells the bot a redemption's actions are still running, via POST /api/companion/rewards/progress.</summary>
    /// <param name="redemptionId">The ID of the redemption whose actions are still running.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is true if the bot restarted its acknowledgement timeout; false if it's no longer waiting for the redemption.</returns>
    public Task<bool> ReportRedemptionProgressAsync(string redemptionId, CancellationToken cancellationToken)
        => ReportProgressAsync("/api/companion/rewards/progress", redemptionId, "Reporting redemption progress", cancellationToken);

    /// <summary>
    /// Asks the bot to revoke <paramref name="token"/> (POST /api/companion/token/revoke), giving
    /// up after <see cref="RevokeTimeout"/>. Never throws for a network failure or timeout.
    /// </summary>
    /// <param name="token">The bearer token to revoke, sent as the request's own credentials.</param>
    /// <param name="cancellationToken">Cancels the revoke request.</param>
    /// <returns>A task whose result is <see cref="LogoutResult.Revoked"/> when the bot confirmed it (or answered 401), otherwise <see cref="LogoutResult.NotConfirmed"/>.</returns>
    public async Task<LogoutResult> RevokeTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RevokeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_botHost, "/api/companion/token/revoke"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            // 401 means the bot doesn't know the token or already revoked it: either way it's dead.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized
                ? LogoutResult.Revoked
                : LogoutResult.NotConfirmed;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return LogoutResult.NotConfirmed;
        }
    }

    /// <summary>POSTs <c>{ eventId }</c> to a progress route and returns the bot's <c>extended</c> flag.</summary>
    /// <param name="path">The bot's progress route, e.g. <c>/api/companion/commands/progress</c>.</param>
    /// <param name="eventId">The ID of the event whose actions are still running.</param>
    /// <param name="operationDescription">Names the request in error messages, e.g. "Reporting command progress".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is true if the bot restarted its acknowledgement timeout; false if it's no longer waiting for the event.</returns>
    private async Task<bool> ReportProgressAsync(string path, string eventId, string operationDescription, CancellationToken cancellationToken)
    {
        ProgressResponse parsed = await PostJsonAsync<ProgressResponse>(
            path, new { eventId }, operationDescription, cancellationToken).ConfigureAwait(false);
        return parsed.Extended;
    }

    /// <summary>
    /// Sends an authenticated GET to a companion API path and deserializes the response body as
    /// <typeparamref name="TResponse"/>. Every <typeparamref name="TResponse"/> carries an
    /// <see cref="IApiEnvelope.Ok"/>/<see cref="IApiEnvelope.Error"/> envelope, checked here so an
    /// HTTP 200 the server marked <c>ok: false</c> throws instead of silently looking like an
    /// empty/absent result.
    /// </summary>
    /// <typeparam name="TResponse">The response body's shape.</typeparam>
    /// <param name="path">The companion API path, relative to the bot host.</param>
    /// <param name="operationDescription">Names the request in error messages, e.g. "Fetching rewards".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is the parsed response, whose envelope has <c>ok: true</c>.</returns>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status, an unparseable body, or an <c>ok: false</c> envelope.</exception>
    private Task<TResponse> FetchAsync<TResponse>(string path, string operationDescription, CancellationToken cancellationToken)
        where TResponse : class, IApiEnvelope
        => SendAsync<TResponse>(HttpMethod.Get, path, null, operationDescription, cancellationToken);

    /// <summary>
    /// Serializes <paramref name="body"/> to JSON and POSTs it to a companion API path, checking
    /// the response envelope like <see cref="FetchAsync{TResponse}"/>.
    /// </summary>
    /// <typeparam name="TResponse">The response body's shape.</typeparam>
    /// <param name="path">The companion API path, relative to the bot host.</param>
    /// <param name="body">The request body; its property names become the JSON field names the bot reads.</param>
    /// <param name="operationDescription">Names the request in error messages, e.g. "Acknowledging command".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is the parsed response, whose envelope has <c>ok: true</c>.</returns>
    private Task<TResponse> PostJsonAsync<TResponse>(string path, object body, string operationDescription, CancellationToken cancellationToken)
        where TResponse : class, IApiEnvelope
        => SendAsync<TResponse>(HttpMethod.Post, path, JsonSerializer.Serialize(body), operationDescription, cancellationToken);

    /// <summary>
    /// Sends an authenticated request (with an optional JSON body) to a companion API path and
    /// checks its envelope — see <see cref="FetchAsync{TResponse}"/>.
    /// </summary>
    /// <typeparam name="TResponse">The response body's shape.</typeparam>
    /// <param name="method">The HTTP method to send.</param>
    /// <param name="path">The companion API path, relative to the bot host.</param>
    /// <param name="jsonBody">The JSON request body, or null to send none.</param>
    /// <param name="operationDescription">Names the request in error messages, e.g. "Acknowledging command".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is the parsed response, whose envelope has <c>ok: true</c>.</returns>
    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method, string path, string? jsonBody, string operationDescription, CancellationToken cancellationToken)
        where TResponse : class, IApiEnvelope
    {
        string? token = _tokenStore.Load();
        if (token is null)
        {
            throw new InvalidOperationException("No companion token saved — log in first.");
        }

        var requestUri = new Uri(_botHost, path);
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse<TResponse>(response.StatusCode, body, operationDescription);
    }

    /// <summary>
    /// Turns a companion API response into its parsed body, or the exception its status, body or
    /// envelope calls for.
    /// </summary>
    /// <typeparam name="TResponse">The response body's shape.</typeparam>
    /// <param name="statusCode">The response's HTTP status.</param>
    /// <param name="body">The response body text.</param>
    /// <param name="operationDescription">Names the request in error messages, e.g. "Fetching rewards".</param>
    /// <returns>The parsed response, whose envelope has <c>ok: true</c>.</returns>
    /// <exception cref="CompanionAuthException"><paramref name="statusCode"/> is 401.</exception>
    /// <exception cref="CompanionApiException">Any other non-success status, an unparseable body, or an <c>ok: false</c> envelope.</exception>
    internal static TResponse ParseResponse<TResponse>(HttpStatusCode statusCode, string body, string operationDescription)
        where TResponse : class, IApiEnvelope
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            throw new CompanionAuthException(JsonHelpers.TryGetString(body, "error") ?? "The companion token was rejected — log in again.", (int)statusCode);
        }

        if ((int)statusCode is < 200 or > 299)
        {
            string message = JsonHelpers.TryGetString(body, "error") ?? $"{operationDescription} failed with status {(int)statusCode}.";
            throw new CompanionApiException(message, (int)statusCode);
        }

        TResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TResponse>(body);
        }
        catch (JsonException)
        {
            throw new CompanionApiException($"{operationDescription} response was not valid JSON.", (int)statusCode);
        }

        if (parsed is null || !parsed.Ok)
        {
            throw new CompanionApiException(parsed?.Error ?? $"{operationDescription} failed.", (int)statusCode);
        }

        return parsed;
    }
}
