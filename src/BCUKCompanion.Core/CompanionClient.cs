using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCUKCompanion.Core.Auth;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using BCUKCompanion.Core.Tokens;

namespace BCUKCompanion.Core;

/// <summary>
/// High-level entry point a companion app wires up: login (OAuth loopback
/// or manual token), then start/stop listening for live redemption events.
/// </summary>
public sealed class CompanionClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _botHost;
    private readonly ITokenStore _tokenStore;
    private readonly LoopbackOAuthClient _oauthClient;

    // Guards _eventLoopCts/_eventLoopTask. Only ever held across plain field
    // reads/writes -- never across an await or a wait on a task -- so it's
    // safe to take even when called from inside a ConnectionStateChanged or
    // RedemptionReceived handler that's running on the listen loop's own call
    // stack.
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _eventLoopCts;
    private Task _eventLoopTask = Task.CompletedTask;

    // Serialises every token store write (and DiscardRejectedToken's compare-and-clear) so a token
    // saved by a login finishing on another thread can't be deleted by a clear meant for the
    // token it replaced. Lock order: this, then _lifecycleLock.
    private readonly object _tokenStoreLock = new();

    // The token the listen loop that's running right now connected with, or null between loops.
    // Loops never overlap, so an event the stream raises comes from the loop using this token.
    private string? _listeningToken;

    /// <summary>The live event stream: subscribe to its events before calling <see cref="StartListening"/>.</summary>
    public CompanionEventStream Events { get; }

    /// <summary>Whether a companion token is saved.</summary>
    public bool IsLoggedIn => _tokenStore.Load() is not null;

    /// <summary>
    /// Raised when the background listen loop ends because
    /// <see cref="CompanionEventStream.RunAsync"/> threw an exception, as
    /// opposed to ending because <see cref="StopListening"/> (or a restart
    /// via <see cref="StartListening"/>) canceled it. Subscribe to this to
    /// observe and react to listen-loop failures instead of having them go
    /// silently unobserved.
    /// </summary>
    public event EventHandler<Exception>? ListenLoopFaulted;

    /// <summary>
    /// Test seam: lets unit tests substitute a controllable fake loop instead
    /// of the real Events.RunAsync, without standing up an HTTP server.
    /// </summary>
    internal Func<string, CancellationToken, Task>? ListenLoopOverride { get; set; }

    /// <summary>
    /// Test seam: lets tests await "the loop has fully unwound, including any
    /// ListenLoopFaulted dispatch" deterministically, without a fixed sleep.
    /// </summary>
    internal Task CurrentLoopTask
    {
        get { lock (_lifecycleLock) { return _eventLoopTask; } }
    }

    /// <exception cref="ArgumentException"><paramref name="botHost"/> is not an HTTPS URL.</exception>
    public CompanionClient(Uri botHost, ITokenStore tokenStore, HttpClient? httpClient = null)
    {
        if (!botHost.IsAbsoluteUri || botHost.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("botHost must be an absolute HTTPS URL — the companion API always requires TLS.", nameof(botHost));
        }

        _tokenStore = tokenStore;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _botHost = botHost;
        _oauthClient = new LoopbackOAuthClient(_httpClient, botHost);
        Events = new CompanionEventStream(_httpClient, botHost);
    }

    /// <summary>
    /// Fetches the current list of Twitch channel-point custom rewards from
    /// GET /api/companion/rewards. The server does not filter out paused rewards, so
    /// callers that only want active ones should filter on <see cref="Reward.IsEnabled"/>
    /// themselves. There's no server-side caching, so avoid polling this aggressively —
    /// it's meant for app-start or occasional refreshes, not real-time updates (use
    /// <see cref="Events"/> for that).
    /// </summary>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status or an unparseable body.</exception>
    public async Task<IReadOnlyList<Reward>> GetRewardsAsync(CancellationToken cancellationToken = default)
    {
        RewardsResponse parsed = await FetchAsync<RewardsResponse>(
            "/api/companion/rewards", "Fetching rewards", cancellationToken).ConfigureAwait(false);

        return (IReadOnlyList<Reward>?)parsed.Rewards ?? Array.Empty<Reward>();
    }

    /// <summary>
    /// Fetches streamer activity events (follow/sub/resub/giftsub/raid — no redemptions)
    /// missed while disconnected, from GET /api/companion/events/recent. Meant for a
    /// one-off catch-up call; <see cref="Events"/> already backfills this automatically
    /// on every (re)connect, so most callers won't need to call this directly.
    /// </summary>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status or an unparseable body.</exception>
    public async Task<IReadOnlyList<CompanionActivityEvent>> GetRecentActivityEventsAsync(CancellationToken cancellationToken = default)
    {
        RecentActivityResponse parsed = await FetchAsync<RecentActivityResponse>(
            "/api/companion/events/recent", "Fetching recent activity", cancellationToken).ConfigureAwait(false);

        return (IReadOnlyList<CompanionActivityEvent>?)parsed.Events ?? Array.Empty<CompanionActivityEvent>();
    }

    /// <summary>
    /// Fetches the streamer's advanced commands from GET /api/companion/commands — e.g. to
    /// offer command names when mapping a command to actions. An empty list when the logged-in
    /// user isn't a streamer.
    /// </summary>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status or an unparseable body.</exception>
    public async Task<IReadOnlyList<AdvancedCommand>> GetAdvancedCommandsAsync(CancellationToken cancellationToken = default)
    {
        CommandsResponse parsed = await FetchAsync<CommandsResponse>(
            "/api/companion/commands", "Fetching advanced commands", cancellationToken).ConfigureAwait(false);

        return (IReadOnlyList<AdvancedCommand>?)parsed.Commands ?? Array.Empty<AdvancedCommand>();
    }

    /// <summary>
    /// Reports whether this app ran an <see cref="AdvancedCommandEvent"/>'s actions, via
    /// POST /api/companion/commands/ack. A failure (or no acknowledgement within the bot's
    /// timeout) refunds the viewer.
    /// </summary>
    /// <param name="eventId">The event's <see cref="AdvancedCommandEvent.Id"/>.</param>
    /// <param name="ok">Whether every action succeeded.</param>
    /// <param name="error">Why not, when <paramref name="ok"/> is false.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>True if the acknowledgement resolved the use; false if the bot had already resolved it (e.g. timed out).</returns>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status or an unparseable body.</exception>
    public async Task<bool> AckAdvancedCommandAsync(string eventId, bool ok, string? error = null, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { eventId, ok, error });
        AckResponse parsed = await SendAsync<AckResponse>(
            HttpMethod.Post, "/api/companion/commands/ack", body, "Acknowledging command", cancellationToken).ConfigureAwait(false);
        return parsed.Resolved;
    }

    /// <summary>
    /// Reports whether this app ran the actions for a <see cref="RedemptionEvent"/> sent with
    /// <see cref="RedemptionEvent.AckRequired"/>, via POST /api/companion/rewards/ack. Success
    /// completes the redemption; a failure (or no acknowledgement within the bot's timeout)
    /// cancels it and refunds the viewer.
    /// </summary>
    /// <param name="redemptionId">The event's <see cref="RedemptionEvent.Id"/>.</param>
    /// <param name="ok">Whether every action succeeded.</param>
    /// <param name="error">Why not, when <paramref name="ok"/> is false.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>True if the acknowledgement resolved the redemption; false if the bot had already resolved it (e.g. timed out).</returns>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status or an unparseable body.</exception>
    public async Task<bool> AckRedemptionAsync(string redemptionId, bool ok, string? error = null, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { eventId = redemptionId, ok, error });
        AckResponse parsed = await SendAsync<AckResponse>(
            HttpMethod.Post, "/api/companion/rewards/ack", body, "Acknowledging redemption", cancellationToken).ConfigureAwait(false);
        return parsed.Resolved;
    }

    /// <summary>
    /// Tells the bot this app is still running an <see cref="AdvancedCommandEvent"/>'s actions, via
    /// POST /api/companion/commands/progress, so a long run (e.g. Delay actions) isn't refunded before
    /// <see cref="AckAdvancedCommandAsync"/> is sent. Each call restarts the bot's acknowledgement
    /// timeout, up to a cap the bot sets.
    /// </summary>
    /// <param name="eventId">The event's <see cref="AdvancedCommandEvent.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>True if the timeout was restarted; false if the bot is no longer waiting for this event.</returns>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status (including 404 from a bot that predates progress pings) or an unparseable body.</exception>
    public Task<bool> ReportCommandProgressAsync(string eventId, CancellationToken cancellationToken = default)
        => ReportProgressAsync("/api/companion/commands/progress", eventId, "Reporting command progress", cancellationToken);

    /// <summary>
    /// Tells the bot this app is still running the actions for a <see cref="RedemptionEvent"/> sent with
    /// <see cref="RedemptionEvent.AckRequired"/>, via POST /api/companion/rewards/progress — see
    /// <see cref="ReportCommandProgressAsync"/>.
    /// </summary>
    /// <param name="redemptionId">The event's <see cref="RedemptionEvent.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>True if the timeout was restarted; false if the bot is no longer waiting for this redemption.</returns>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status or an unparseable body.</exception>
    public Task<bool> ReportRedemptionProgressAsync(string redemptionId, CancellationToken cancellationToken = default)
        => ReportProgressAsync("/api/companion/rewards/progress", redemptionId, "Reporting redemption progress", cancellationToken);

    /// <summary>POSTs <c>{ eventId }</c> to a progress route and returns the bot's <c>extended</c> flag.</summary>
    private async Task<bool> ReportProgressAsync(string path, string eventId, string operationDescription, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { eventId });
        ProgressResponse parsed = await SendAsync<ProgressResponse>(
            HttpMethod.Post, path, body, operationDescription, cancellationToken).ConfigureAwait(false);
        return parsed.Extended;
    }

    /// <summary>
    /// Sends an authenticated GET to a companion API path and deserializes the response body as
    /// <typeparamref name="TResponse"/>. Shared by every simple request/response companion
    /// endpoint (as opposed to <see cref="Events"/>, which is long-lived/streaming). Every
    /// <typeparamref name="TResponse"/> carries an <see cref="IApiEnvelope.Ok"/>/
    /// <see cref="IApiEnvelope.Error"/> envelope, checked here so an HTTP 200 the server marked
    /// <c>ok: false</c> throws instead of silently looking like an empty/absent result.
    /// </summary>
    /// <exception cref="InvalidOperationException">No companion token is saved.</exception>
    /// <exception cref="CompanionAuthException">The saved token was rejected (401).</exception>
    /// <exception cref="CompanionApiException">The server returned a non-success status, an unparseable body, or an <c>ok: false</c> envelope.</exception>
    private Task<TResponse> FetchAsync<TResponse>(string path, string operationDescription, CancellationToken cancellationToken)
        where TResponse : class, IApiEnvelope
        => SendAsync<TResponse>(HttpMethod.Get, path, null, operationDescription, cancellationToken);

    /// <summary>
    /// Sends an authenticated request (with an optional JSON body) to a companion API path and
    /// checks its envelope — see <see cref="FetchAsync{TResponse}"/>.
    /// </summary>
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

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CompanionAuthException(JsonHelpers.TryGetString(body, "error") ?? "The companion token was rejected — log in again.", (int)response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            string message = JsonHelpers.TryGetString(body, "error") ?? $"{operationDescription} failed with status {(int)response.StatusCode}.";
            throw new CompanionApiException(message, (int)response.StatusCode);
        }

        TResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TResponse>(body);
        }
        catch (JsonException)
        {
            throw new CompanionApiException($"{operationDescription} response was not valid JSON.", (int)response.StatusCode);
        }

        if (parsed is null || !parsed.Ok)
        {
            throw new CompanionApiException(parsed?.Error ?? $"{operationDescription} failed.", (int)response.StatusCode);
        }

        return parsed;
    }

    /// <summary>Common envelope shape (<c>{ ok, error, ... }</c>) every companion request/response API returns.</summary>
    private interface IApiEnvelope
    {
        /// <summary>Whether the request succeeded.</summary>
        bool Ok { get; }

        /// <summary>The bot's error message, when <see cref="Ok"/> is false.</summary>
        string? Error { get; }
    }

    /// <summary>The response of GET /api/companion/events/recent.</summary>
    private sealed class RecentActivityResponse : IApiEnvelope
    {
        /// <summary>Whether the request succeeded.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>The missed activity events.</summary>
        [JsonPropertyName("events")]
        public List<CompanionActivityEvent>? Events { get; set; }

        /// <summary>The bot's error message, when <see cref="Ok"/> is false.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>The response of GET /api/companion/rewards.</summary>
    private sealed class RewardsResponse : IApiEnvelope
    {
        /// <summary>Whether the request succeeded.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>The channel's custom rewards.</summary>
        [JsonPropertyName("rewards")]
        public List<Reward>? Rewards { get; set; }

        /// <summary>The bot's error message, when <see cref="Ok"/> is false.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>The response of GET /api/companion/commands.</summary>
    private sealed class CommandsResponse : IApiEnvelope
    {
        /// <summary>Whether the request succeeded.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>The streamer's advanced commands.</summary>
        [JsonPropertyName("commands")]
        public List<AdvancedCommand>? Commands { get; set; }

        /// <summary>The bot's error message, when <see cref="Ok"/> is false.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>The response of the command and redemption ack routes.</summary>
    private sealed class AckResponse : IApiEnvelope
    {
        /// <summary>Whether the request succeeded.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>Whether this acknowledgement resolved the event (false if the bot already had, e.g. on timeout).</summary>
        [JsonPropertyName("resolved")]
        public bool Resolved { get; set; }

        /// <summary>The bot's error message, when <see cref="Ok"/> is false.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>The response of the command and redemption progress routes.</summary>
    private sealed class ProgressResponse : IApiEnvelope
    {
        /// <summary>Whether the request succeeded.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>Whether the bot restarted its acknowledgement timeout.</summary>
        [JsonPropertyName("extended")]
        public bool Extended { get; set; }

        /// <summary>The bot's error message, when <see cref="Ok"/> is false.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>Runs the loopback OAuth login flow (Option A) and saves the resulting token.</summary>
    public async Task LoginWithBrowserAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default)
    {
        string token = await _oauthClient.LoginAsync(openBrowser, cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_tokenStoreLock)
        {
            _tokenStore.Save(token);
        }
    }

    /// <summary>Saves a token the user pasted in from the dashboard's manual-token page (Option B).</summary>
    public void SetManualToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token must not be blank.", nameof(token));
        }

        lock (_tokenStoreLock)
        {
            _tokenStore.Save(token);
        }
    }

    /// <summary>
    /// Stops listening and discards the stored token, locally only: the token stays valid on the
    /// bot. Use <see cref="LogoutAsync"/> for a user logging out, so a copy of the token stops
    /// working too; this is for when the bot already rejected the token, or can't be reached.
    /// </summary>
    public void Logout()
    {
        lock (_tokenStoreLock)
        {
            // Token first, so a failed delete leaves the app logged in and still listening
            // rather than disconnected while holding the token.
            _tokenStore.Clear();
            StopListening();
        }
    }

    /// <summary>
    /// Discards the saved token after the bot rejected the one the listen loop connected with:
    /// call it from a <see cref="CompanionEventStream.ConnectionStateChanged"/> handler for
    /// <see cref="CompanionConnectionState.AuthenticationFailed"/>. Only that rejected token is
    /// discarded (and listening stopped): if a newer one was saved meanwhile, e.g. the user logged
    /// in again while the old loop was still connecting, it's kept and nothing is changed.
    /// </summary>
    /// <returns>True if the rejected token was discarded; false if a different token (or none) is saved now.</returns>
    /// <exception cref="IOException">The token file couldn't be read or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The token file couldn't be read or deleted.</exception>
    public bool DiscardRejectedToken()
    {
        string? rejected;
        lock (_lifecycleLock)
        {
            rejected = _listeningToken;
        }

        // Compared and cleared under one lock, so a login saving a new token can't land in between.
        lock (_tokenStoreLock)
        {
            if (rejected is null || _tokenStore.Load() != rejected)
            {
                return false;
            }

            Logout();
            return true;
        }
    }

    /// <summary>
    /// Logs out: discards the stored token, stops listening, then asks the bot to revoke it
    /// (POST /api/companion/token/revoke) so a copy of it stops working too. The local logout
    /// happens before this returns, so a token file that can't be read or deleted throws here
    /// rather than from the returned task. Revoking is best-effort: its failure (offline, a bot
    /// that predates the endpoint) only leaves the token valid on the bot, as <see cref="Logout"/> does.
    /// </summary>
    /// <returns>Whether there was a token, and whether the bot confirmed it no longer works.</returns>
    /// <exception cref="IOException">The token file couldn't be read or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The token file couldn't be read or deleted.</exception>
    public Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        string? token;
        lock (_tokenStoreLock)
        {
            token = _tokenStore.Load();
            Logout();
        }

        return token is null ? Task.FromResult(LogoutResult.NoToken) : RevokeAsync(token, cancellationToken);
    }

    /// <summary>Asks the bot to revoke <paramref name="token"/>, giving up after <see cref="RevokeTimeout"/>.</summary>
    private async Task<LogoutResult> RevokeAsync(string token, CancellationToken cancellationToken)
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

    /// <summary>How long <see cref="LogoutAsync"/> waits for the bot to revoke the token.</summary>
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts (or restarts) the background SSE listen loop using the saved
    /// token. Returns immediately without blocking: the previous loop (if
    /// any) is canceled and guaranteed to fully unwind before the new loop's
    /// first connection attempt runs, so listen loops never overlap. Safe to
    /// call from inside a <see cref="CompanionEventStream.RedemptionReceived"/>
    /// or <see cref="CompanionEventStream.ConnectionStateChanged"/> handler.
    /// </summary>
    public void StartListening()
    {
        string? token = _tokenStore.Load();
        if (token is null)
        {
            throw new InvalidOperationException("No companion token saved — log in first.");
        }

        lock (_lifecycleLock)
        {
            Task previousLoop = CancelCurrentLoopLocked();
            var cts = new CancellationTokenSource();
            _eventLoopCts = cts;
            // Task.Run forces a real async boundary so the field writes above are
            // guaranteed to be visible (and the lock released) before any loop body
            // code -- including a synchronous ConnectionStateChanged dispatch -- runs.
            // Without this, a reentrant StartListening()/StopListening() call from
            // inside that dispatch could run while this method is still on the stack.
            _eventLoopTask = Task.Run(() => RunAfterPreviousAsync(previousLoop, token, cts));
        }
    }

    /// <summary>
    /// Requests that the current listen loop stop. Only signals cancellation
    /// and returns immediately -- it never waits for the loop to finish
    /// unwinding -- so it's safe to call from inside a
    /// <see cref="CompanionEventStream.RedemptionReceived"/> or
    /// <see cref="CompanionEventStream.ConnectionStateChanged"/> handler
    /// without risking a deadlock. Subscribe to <see cref="ListenLoopFaulted"/>
    /// to observe whether the loop being stopped ended in failure.
    /// </summary>
    public void StopListening()
    {
        lock (_lifecycleLock)
        {
            CancelCurrentLoopLocked();
        }
    }

    /// <summary>Must be called while holding <see cref="_lifecycleLock"/>.</summary>
    private Task CancelCurrentLoopLocked()
    {
        _eventLoopCts?.Cancel();
        _eventLoopCts = null;
        return _eventLoopTask;
    }

    /// <summary>Waits for the previous listen loop to unwind, then runs the next one with <paramref name="token"/> until it's canceled or fails. A failure is raised as <see cref="ListenLoopFaulted"/>.</summary>
    private async Task RunAfterPreviousAsync(Task previousLoop, string token, CancellationTokenSource cts)
    {
        try
        {
            await previousLoop.ConfigureAwait(false);
        }
        catch
        {
            // The previous loop's own failure (if any) was already surfaced
            // via ListenLoopFaulted while it was running; nothing further to
            // observe here -- we're only waiting for it to fully unwind.
        }

        lock (_lifecycleLock)
        {
            _listeningToken = token;
        }

        try
        {
            await (ListenLoopOverride ?? Events.RunAsync)(token, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Expected: StopListening() or a restart canceled this loop.
        }
        catch (Exception ex)
        {
            ListenLoopFaulted?.Invoke(this, ex);
        }
        finally
        {
            lock (_lifecycleLock)
            {
                // If this loop exited on its own (fault or, hypothetically, a
                // clean return) rather than via StopListening()/a restart, the
                // field still points at this cts -- clear it so a later
                // StopListening() doesn't try to Cancel() what we're about to
                // dispose below.
                if (ReferenceEquals(_eventLoopCts, cts))
                {
                    _eventLoopCts = null;
                }

                // The next loop only starts once this one has unwound, so this is still its token.
                _listeningToken = null;
            }

            // Deferred until the loop has genuinely exited -- disposing eagerly in
            // CancelCurrentLoopLocked() could race with this loop still registering
            // callbacks against cts.Token (e.g. inside Task.Delay), which would throw
            // ObjectDisposedException instead of observing cancellation.
            cts.Dispose();
        }
    }

    /// <summary>Stops listening and disposes the HTTP client if this instance created it.</summary>
    public void Dispose()
    {
        StopListening();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
