using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCUKCompanion.Core.Models;
using System.Linq;

namespace BCUKCompanion.Core.Events;

/// <summary>
/// Opens and maintains the persistent SSE connection to
/// GET /api/companion/events, reconnecting with backoff on transient
/// failures, and stopping when the server reports the token is no longer
/// valid (401).
/// </summary>
public sealed class CompanionEventStream
{
    private readonly HttpClient _httpClient;
    private readonly Uri _botHost;

    private static readonly HashSet<string> ActivityEventTypes =
        new(StringComparer.Ordinal) { "follow", "sub", "resub", "giftsub", "raid" };

    // High-water mark of the newest activity event id already surfaced (live or backfilled),
    // so a post-reconnect backfill from GET /api/companion/events/recent doesn't re-raise
    // ActivityReceived for events already delivered live before the drop. CompanionActivityEvent.Id
    // is the bot's streamer_event_log.id, assigned in strictly increasing insertion order, so a
    // plain high-water mark is exact -- unlike the OccurredAt-based heuristic this replaced
    // (second-precision timestamps can't tell two distinct same-second events apart).
    private long _lastActivityEventId;

    public event EventHandler<RedemptionEvent>? RedemptionReceived;
    public event EventHandler<CompanionActivityEvent>? ActivityReceived;
    public event EventHandler<CompanionConnectionState>? ConnectionStateChanged;

    public CompanionEventStream(HttpClient httpClient, Uri botHost)
    {
        _httpClient = httpClient;
        _botHost = botHost;
    }

    /// <summary>
    /// Runs the connect/read/reconnect loop until <paramref name="cancellationToken"/>
    /// is canceled or the server reports the token is invalid/revoked (401).
    /// </summary>
    public async Task RunAsync(string token, CancellationToken cancellationToken = default)
    {
        int attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            ConnectionStateChanged?.Invoke(this, CompanionConnectionState.Connecting);

            (bool shouldStop, bool wasConnected) = await ConnectOnceAsync(token, cancellationToken).ConfigureAwait(false);
            if (shouldStop)
            {
                return;
            }

            ConnectionStateChanged?.Invoke(this, CompanionConnectionState.Disconnected);

            if (wasConnected)
            {
                attempt = 0;
            }

            try
            {
                await Task.Delay(ReconnectBackoff.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            attempt++;
        }
    }

    /// <summary>Connects once. ShouldStop is true if the caller should stop retrying (auth failure).</summary>
    private async Task<(bool ShouldStop, bool WasConnected)> ConnectOnceAsync(string token, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_botHost, "/api/companion/events");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 60s idle timeout: the server pings every 25s, so two missed pings
        // means the connection is dead even without a TCP-level signal.
        using var idleCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleCts.Token);
        ResetIdleTimer(idleCts);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
        }
        // Only genuine transient connect failures are treated as "try again": a failed DNS
        // lookup/connect/TLS handshake or a mid-request drop (HttpRequestException, possibly
        // wrapping a SocketException), the idle-timeout/HttpClient-timeout cancellation that
        // surfaces as TaskCanceledException (the `when` clause already excludes the caller's
        // own cancellationToken, so this can't be mistaken for a deliberate stop), a bare
        // SocketException that reaches here without an HttpRequestException wrapper, or an
        // IOException from the underlying connection. Anything else (a bug elsewhere in this
        // method) propagates instead of being silently swallowed and retried forever.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or IOException or TaskCanceledException or System.Net.Sockets.SocketException)
        {
            return (ShouldStop: false, WasConnected: false);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ConnectionStateChanged?.Invoke(this, CompanionConnectionState.AuthenticationFailed);
                return (ShouldStop: true, WasConnected: false);
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                ConnectionStateChanged?.Invoke(this, CompanionConnectionState.RateLimited);
                return (ShouldStop: false, WasConnected: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (ShouldStop: false, WasConnected: false);
            }

            ConnectionStateChanged?.Invoke(this, CompanionConnectionState.Connected);
            ResetIdleTimer(idleCts);

            // The server unilaterally closes SSE connections on token revoke/reissue,
            // and SSE itself sends no backfill -- so every (re)connect, fetch activity
            // missed while disconnected before entering the live read loop.
            await FetchRecentActivityAsync(token, linkedCts.Token).ConfigureAwait(false);

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                using var streamReader = new StreamReader(stream);
                var sseReader = new SseEventReader(streamReader);

                await foreach (SseEvent sseEvent in sseReader.ReadEventsAsync(
                    onActivity: () => ResetIdleTimer(idleCts),
                    cancellationToken: linkedCts.Token).ConfigureAwait(false))
                {
                    HandleEvent(sseEvent);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Idle timeout fired — fall through to reconnect.
            }
            catch (IOException)
            {
                // Connection dropped — fall through to reconnect.
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
            {
                // Mid-stream connection reset, possibly surfaced as HttpRequestException
                // rather than IOException depending on the transport — fall through to reconnect,
                // same as the connect-phase catch above.
            }

            return (ShouldStop: false, WasConnected: true);
        }
    }

    private static void ResetIdleTimer(CancellationTokenSource idleCts)
    {
        try
        {
            idleCts.CancelAfter(TimeSpan.FromSeconds(60));
        }
        catch (ObjectDisposedException)
        {
            // Stream already finished tearing down.
        }
    }

    /// <summary>
    /// Best-effort fetch of activity events missed while disconnected, via
    /// GET /api/companion/events/recent. Discards anything the server marked
    /// unsuccessful (<c>ok: false</c>) or that fails the same validation live
    /// events go through, then raises <see cref="ActivityReceived"/> only for
    /// records not already delivered (see <see cref="MarkSeen"/>). Errors are
    /// swallowed — this must never fail the connection attempt itself.
    /// </summary>
    private async Task FetchRecentActivityAsync(string token, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_botHost, "/api/companion/events/recent");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        RecentActivityResponse? parsed;
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            parsed = JsonSerializer.Deserialize<RecentActivityResponse>(body);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (parsed is not { Ok: true, Events: not null })
        {
            return;
        }

        foreach (CompanionActivityEvent activity in parsed.Events.Where(IsValidActivity).OrderBy(e => e.Id))
        {
            if (MarkSeen(activity))
            {
                ActivityReceived?.Invoke(this, activity);
            }
        }
    }

    private void HandleEvent(SseEvent sseEvent)
    {
        if (string.IsNullOrWhiteSpace(sseEvent.Data))
        {
            return;
        }

        string? type = JsonHelpers.TryGetString(sseEvent.Data, "type");
        if (type is null)
        {
            return;
        }

        if (ActivityEventTypes.Contains(type))
        {
            HandleActivityEvent(sseEvent.Data);
        }
        else if (type == "channel_points_redemption")
        {
            HandleRedemptionEvent(sseEvent.Data);
        }

        // Any other type is ignored for forward compatibility.
    }

    private void HandleActivityEvent(string data)
    {
        CompanionActivityEvent? activity;
        try
        {
            activity = JsonSerializer.Deserialize<CompanionActivityEvent>(data);
        }
        catch (JsonException)
        {
            return;
        }

        if (!IsValidActivity(activity))
        {
            return;
        }

        // A live event can duplicate one already surfaced by the reconnect backfill --
        // the backfill request races the SSE connection, so the server can buffer this
        // same event on the wire before the backfill response comes back. Only raise it
        // if MarkSeen says it's genuinely new.
        if (MarkSeen(activity!))
        {
            ActivityReceived?.Invoke(this, activity!);
        }
    }

    private static bool IsValidActivity(CompanionActivityEvent? activity) =>
        activity is not null
        && ActivityEventTypes.Contains(activity.Type)
        && !string.IsNullOrWhiteSpace(activity.DisplayName)
        && activity.OccurredAt != default
        && activity.Id > 0;

    /// <summary>
    /// Records <paramref name="activity"/> against the id high-water mark shared by both live
    /// and backfilled dispatch, returning whether its id is newer than any seen so far. Both
    /// <see cref="HandleActivityEvent"/> and <see cref="FetchRecentActivityAsync"/> only raise
    /// <see cref="ActivityReceived"/> when this returns true, so the same activity arriving via
    /// both paths (the backfill request races the live SSE connection) is only delivered once.
    /// </summary>
    private bool MarkSeen(CompanionActivityEvent activity)
    {
        if (activity.Id <= _lastActivityEventId)
        {
            return false;
        }

        _lastActivityEventId = activity.Id;
        return true;
    }

    private void HandleRedemptionEvent(string data)
    {
        RedemptionEvent? redemption;
        try
        {
            redemption = JsonSerializer.Deserialize<RedemptionEvent>(data);
        }
        catch (JsonException)
        {
            return;
        }

        if (redemption is not null && IsComplete(redemption))
        {
            RedemptionReceived?.Invoke(this, redemption);
        }
    }

    private sealed class RecentActivityResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("events")]
        public List<CompanionActivityEvent>? Events { get; set; }
    }

    private static bool IsComplete(RedemptionEvent redemption) =>
        redemption.RedeemedAt != default
        && RequiredStringFields(redemption).All(field => !string.IsNullOrWhiteSpace(field));

    private static IEnumerable<string> RequiredStringFields(RedemptionEvent redemption)
    {
        yield return redemption.Type;
        yield return redemption.RewardId;
        yield return redemption.RewardTitle;
        yield return redemption.UserLogin;
        yield return redemption.UserName;
    }
}
