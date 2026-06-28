using BCUKCompanion.Core.Auth;
using BCUKCompanion.Core.Events;
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

    public CompanionClient(Uri botHost, ITokenStore tokenStore, HttpClient? httpClient = null)
    {
        _tokenStore = tokenStore;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _oauthClient = new LoopbackOAuthClient(_httpClient, botHost);
        Events = new CompanionEventStream(_httpClient, botHost);
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
            _eventLoopTask = RunAfterPreviousAsync(previousLoop, token, cts.Token);
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
        _eventLoopCts?.Dispose();
        _eventLoopCts = null;
        return _eventLoopTask;
    }

    private async Task RunAfterPreviousAsync(Task previousLoop, string token, CancellationToken cancellationToken)
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
            await (ListenLoopOverride ?? Events.RunAsync)(token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected: StopListening() or a restart canceled this loop.
        }
        catch (Exception ex)
        {
            ListenLoopFaulted?.Invoke(this, ex);
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
