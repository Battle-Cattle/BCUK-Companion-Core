using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

    public event EventHandler<RedemptionEvent>? RedemptionReceived;
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
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

    private void HandleEvent(SseEvent sseEvent)
    {
        if (string.IsNullOrWhiteSpace(sseEvent.Data))
        {
            return;
        }

        RedemptionEvent? redemption;
        try
        {
            redemption = JsonSerializer.Deserialize<RedemptionEvent>(sseEvent.Data);
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
