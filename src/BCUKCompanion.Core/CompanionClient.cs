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

    public CompanionEventStream Events { get; }

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

    // Test seam: lets unit tests substitute a controllable fake loop instead
    // of the real Events.RunAsync, without standing up an HTTP server.
    internal Func<string, CancellationToken, Task>? ListenLoopOverride { get; set; }

    // Test seam: lets tests await "the loop has fully unwound, including any
    // ListenLoopFaulted dispatch" deterministically, without a fixed sleep.
    internal Task CurrentLoopTask
    {
        get { lock (_lifecycleLock) { return _eventLoopTask; } }
    }

    public CompanionClient(Uri botHost, ITokenStore tokenStore, HttpClient? httpClient = null)
    {
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
    private async Task<TResponse> FetchAsync<TResponse>(string path, string operationDescription, CancellationToken cancellationToken)
        where TResponse : class, IApiEnvelope
    {
        string? token = _tokenStore.Load();
        if (token is null)
        {
            throw new InvalidOperationException("No companion token saved — log in first.");
        }

        var requestUri = new Uri(_botHost, path);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        bool Ok { get; }

        string? Error { get; }
    }

    private sealed class RecentActivityResponse : IApiEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("events")]
        public List<CompanionActivityEvent>? Events { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class RewardsResponse : IApiEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("rewards")]
        public List<Reward>? Rewards { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>Runs the loopback OAuth login flow (Option A) and saves the resulting token.</summary>
    public async Task LoginWithBrowserAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default)
    {
        string token = await _oauthClient.LoginAsync(openBrowser, cancellationToken: cancellationToken).ConfigureAwait(false);
        _tokenStore.Save(token);
    }

    /// <summary>Saves a token the user pasted in from the dashboard's manual-token page (Option B).</summary>
    public void SetManualToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token must not be blank.", nameof(token));
        }

        _tokenStore.Save(token);
    }

    /// <summary>Stops listening and discards the stored token.</summary>
    public void Logout()
    {
        StopListening();
        _tokenStore.Clear();
    }

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
            }

            // Deferred until the loop has genuinely exited -- disposing eagerly in
            // CancelCurrentLoopLocked() could race with this loop still registering
            // callbacks against cts.Token (e.g. inside Task.Delay), which would throw
            // ObjectDisposedException instead of observing cancellation.
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        StopListening();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
