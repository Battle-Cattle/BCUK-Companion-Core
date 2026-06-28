using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BCUKCompanion.Core.Models;

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

            bool shouldStop = await ConnectOnceAsync(token, cancellationToken).ConfigureAwait(false);
            if (shouldStop)
            {
                return;
            }

            attempt++;
            ConnectionStateChanged?.Invoke(this, CompanionConnectionState.Disconnected);

            try
            {
                await Task.Delay(ReconnectBackoff.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Connects once. Returns true if the caller should stop retrying (auth failure).</summary>
    private async Task<bool> ConnectOnceAsync(string token, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_botHost, "/api/companion/events");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 60s idle timeout: the server pings every 25s, so two missed pings
        // means the connection is dead even without a TCP-level signal.
        using var idleCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ConnectionStateChanged?.Invoke(this, CompanionConnectionState.AuthenticationFailed);
                return true;
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                ConnectionStateChanged?.Invoke(this, CompanionConnectionState.RateLimited);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            ConnectionStateChanged?.Invoke(this, CompanionConnectionState.Connected);
            ResetIdleTimer(idleCts);

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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

            return false;
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

        if (redemption is not null)
        {
            RedemptionReceived?.Invoke(this, redemption);
        }
    }
}
