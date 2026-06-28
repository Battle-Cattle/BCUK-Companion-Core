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

    private CancellationTokenSource? _eventLoopCts;
    private Task? _eventLoopTask;

    public CompanionEventStream Events { get; }

    public bool IsLoggedIn => _tokenStore.Load() is not null;

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
        _tokenStore.Save(token);
    }

    /// <summary>Stops listening and discards the stored token.</summary>
    public void Logout()
    {
        StopListening();
        _tokenStore.Clear();
    }

    /// <summary>Starts (or restarts) the background SSE listen loop using the saved token.</summary>
    public void StartListening()
    {
        string? token = _tokenStore.Load();
        if (token is null)
        {
            throw new InvalidOperationException("No companion token saved — log in first.");
        }

        StopListening();
        _eventLoopCts = new CancellationTokenSource();
        _eventLoopTask = Events.RunAsync(token, _eventLoopCts.Token);
    }

    public void StopListening()
    {
        _eventLoopCts?.Cancel();
        _eventLoopCts?.Dispose();
        _eventLoopCts = null;
        _eventLoopTask = null;
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
