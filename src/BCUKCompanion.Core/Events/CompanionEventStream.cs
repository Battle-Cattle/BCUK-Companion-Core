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

    // Recently delivered advanced command event ids. Their ids are UUIDs (no ordering to keep a
    // high-water mark on), so a small bounded window guards against the same use being raised
    // twice — which would run its actions (and acknowledge) twice.
    private const int SeenCommandEventCapacity = 256;
    private readonly HashSet<string> _seenCommandEventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenCommandEventOrder = new();

    // Test seam: how long the stream may go without receiving anything before it's treated as
    // dead. The server pings every 25s, so 60s means two missed pings.
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public event EventHandler<RedemptionEvent>? RedemptionReceived;
    public event EventHandler<CompanionActivityEvent>? ActivityReceived;
    public event EventHandler<AdvancedCommandEvent>? AdvancedCommandReceived;
    public event EventHandler<CompanionConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised with the new value whenever <see cref="LastActivityEventId"/> advances, so a host can
    /// persist it and seed the next run with it (see <see cref="LastActivityEventId"/>).
    /// </summary>
    public event EventHandler<long>? LastActivityEventIdChanged;

    /// <summary>
    /// The newest activity event id already delivered (gets or sets it). <see cref="ActivityReceived"/> is only raised
    /// for events with a higher id. The connect-time catch-up (GET /api/companion/events/recent)
    /// returns recent events whether or not this process has seen them, so a host that wants to
    /// avoid re-announcing them after a restart persists this value (via
    /// <see cref="LastActivityEventIdChanged"/>) and sets it again before listening.
    /// </summary>
    public long LastActivityEventId
    {
        get => _lastActivityEventId;
        set => _lastActivityEventId = value;
    }

    /// <summary>
    /// When true and <see cref="LastActivityEventId"/> is still 0, the first catch-up only records
    /// the newest event's id instead of raising <see cref="ActivityReceived"/> for everything the
    /// server returns. For a host with no saved <see cref="LastActivityEventId"/> (a first run), so
    /// it doesn't announce old activity. Set before listening.
    /// </summary>
    public bool BaselineOnFirstCatchUp { get; set; }

    /// <summary>Creates a stream that connects to <paramref name="botHost"/>'s event endpoints.</summary>
    /// <param name="httpClient">Used for the SSE connection and the catch-up request.</param>
    /// <param name="botHost">The bot's base URL.</param>
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
        ResetIdleTimer(idleCts, IdleTimeout);

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
            ResetIdleTimer(idleCts, IdleTimeout);

            try
            {
                // The server unilaterally closes SSE connections on token revoke/reissue,
                // and SSE itself sends no backfill -- so every (re)connect, fetch activity
                // missed while disconnected before entering the live read loop. Inside this
                // try so an idle timeout that fires while it's still running reconnects (with
                // backoff) like any other dead connection, instead of escaping RunAsync.
                await FetchRecentActivityAsync(token, linkedCts.Token).ConfigureAwait(false);

                await using Stream stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                using var streamReader = new StreamReader(stream);
                var sseReader = new SseEventReader(streamReader);

                await foreach (SseEvent sseEvent in sseReader.ReadEventsAsync(
                    onActivity: () => ResetIdleTimer(idleCts, IdleTimeout),
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

    private static void ResetIdleTimer(CancellationTokenSource idleCts, TimeSpan idleTimeout)
    {
        try
        {
            idleCts.CancelAfter(idleTimeout);
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

        List<CompanionActivityEvent> valid = parsed.Events.Where(IsValidActivity).OrderBy(e => e.Id).ToList();

        if (BaselineOnFirstCatchUp && _lastActivityEventId == 0)
        {
            // Nothing is known about what was already shown, so treat everything the server
            // returned as seen rather than announcing it. Live events after this still go through.
            BaselineOnFirstCatchUp = false;
            if (valid.Count > 0 && MarkSeen(valid[^1]))
            {
                // No activity to dispatch here, so the host only hears about the new mark.
                LastActivityEventIdChanged?.Invoke(this, valid[^1].Id);
            }
            return;
        }

        foreach (CompanionActivityEvent activity in valid)
        {
            if (MarkSeen(activity))
            {
                DispatchActivity(activity);
            }
        }
    }

    /// <summary>Routes one SSE frame to the redemption, activity or advanced-command handler by its <c>type</c>.</summary>
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
        else if (type == "advanced_command")
        {
            HandleAdvancedCommandEvent(sseEvent.Data);
        }

        // Any other type is ignored for forward compatibility.
    }

    /// <summary>
    /// Parses a live activity frame and dispatches it if it's valid and newer than anything
    /// already delivered.
    /// </summary>
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
            DispatchActivity(activity!);
        }
    }

    /// <summary>
    /// Whether an activity (live or from the catch-up) has a known type, a display name, a
    /// timestamp and a positive id; anything else is dropped rather than shown.
    /// </summary>
    private static bool IsValidActivity(CompanionActivityEvent? activity)
    {
        if (activity is null)
        {
            return false;
        }

        var hasKnownType = ActivityEventTypes.Contains(activity.Type);
        var hasDisplayName = !string.IsNullOrWhiteSpace(activity.DisplayName);
        var hasOccurredAt = activity.OccurredAt != default;
        var hasValidId = activity.Id > 0;

        return hasKnownType && hasDisplayName && hasOccurredAt && hasValidId;
    }

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

    /// <summary>
    /// Raises <see cref="ActivityReceived"/> for a newly seen activity, then
    /// <see cref="LastActivityEventIdChanged"/>. In that order, so a host that persists the id
    /// from the second event has always been handed the activity first: if the process exits in
    /// between, the next run shows the activity again rather than skipping one never shown.
    /// </summary>
    private void DispatchActivity(CompanionActivityEvent activity)
    {
        ActivityReceived?.Invoke(this, activity);
        LastActivityEventIdChanged?.Invoke(this, activity.Id);
    }

    /// <summary>Parses a channel-point redemption frame and raises <see cref="RedemptionReceived"/> if it's complete.</summary>
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

    /// <summary>Parses an <c>advanced_command</c> frame and raises <see cref="AdvancedCommandReceived"/> once per complete, unseen event.</summary>
    private void HandleAdvancedCommandEvent(string data)
    {
        AdvancedCommandEvent? command;
        try
        {
            command = JsonSerializer.Deserialize<AdvancedCommandEvent>(data);
        }
        catch (JsonException)
        {
            return;
        }

        if (command is not null && IsComplete(command) && MarkCommandSeen(command.Id))
        {
            AdvancedCommandReceived?.Invoke(this, command);
        }
    }

    /// <summary>Whether an advanced command event has the fields a handler relies on (id, name, a known source, a timestamp).</summary>
    private static bool IsComplete(AdvancedCommandEvent command) =>
        !string.IsNullOrWhiteSpace(command.Id)
        && !string.IsNullOrWhiteSpace(command.CommandName)
        && command.Source is "chat" or "streamdeck"
        && command.TriggeredAt != default;

    /// <summary>Records an advanced command event id, returning false if it was already delivered.</summary>
    private bool MarkCommandSeen(string eventId)
    {
        if (!_seenCommandEventIds.Add(eventId))
        {
            return false;
        }

        _seenCommandEventOrder.Enqueue(eventId);
        if (_seenCommandEventOrder.Count > SeenCommandEventCapacity)
        {
            _seenCommandEventIds.Remove(_seenCommandEventOrder.Dequeue());
        }

        return true;
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
