using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace BCUKCompanion.Core.Auth;

/// <summary>
/// Implements Option A from companionappsetupguide.md: the RFC 8252
/// loopback-interception OAuth flow. Spins up a local HTTP listener,
/// hands the caller a URL to open in the user's browser, waits for the
/// bot to redirect back with a one-time code, and exchanges that code for
/// a long-lived bearer token. Uses PKCE (RFC 7636, S256): only this process,
/// which holds the verifier, can exchange the code, even if another local
/// process sees it. A bot that predates PKCE ignores the extra parameters.
/// </summary>
public sealed class LoopbackOAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _botHost;

    public LoopbackOAuthClient(HttpClient httpClient, Uri botHost)
    {
        _httpClient = httpClient;
        _botHost = botHost;
    }

    /// <summary>
    /// Runs the full loopback login flow and returns the issued bearer
    /// token. <paramref name="openBrowser"/> is invoked with the login URL
    /// the caller must open (e.g. via <c>Process.Start</c>).
    /// </summary>
    public async Task<string> LoginAsync(
        Func<Uri, Task> openBrowser,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string state = GenerateState();
        string codeVerifier = GenerateState();
        using var listener = new HttpListener();
        int port = LoopbackCallbackListener.StartListenerOnFreePort(listener);
        var redirectUri = new Uri($"http://127.0.0.1:{port}/callback");

        try
        {
            Uri loginUrl = BuildLoginUrl(redirectUri, state, CodeChallenge(codeVerifier));
            await openBrowser(loginUrl).ConfigureAwait(false);

            (HttpListenerContext context, string code) = await LoopbackCallbackListener.WaitForCallbackAsync(
                listener, state, timeout ?? TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            try
            {
                // Don't tell the browser login succeeded until the code has actually
                // been exchanged for a token — otherwise a later exchange failure
                // leaves the user seeing a false "success" page.
                string token = await ExchangeCodeForTokenAsync(code, codeVerifier, cancellationToken).ConfigureAwait(false);
                LoopbackCallbackListener.RespondHtml(context, 200, "<html><body>Login successful — you can close this window and return to the app.</body></html>");
                return token;
            }
            catch (Exception)
            {
                LoopbackCallbackListener.RespondHtml(context, 502, "<html><body>Login failed: the app could not complete sign-in. You can close this window.</body></html>");
                throw;
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>The bot's companion login URL, carrying the loopback redirect, the OAuth <c>state</c> and the PKCE S256 challenge.</summary>
    internal Uri BuildLoginUrl(Uri redirectUri, string state, string codeChallenge)
    {
        string query = $"redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&state={Uri.EscapeDataString(state)}"
            + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256";
        return new Uri(_botHost, $"/companion/login?{query}");
    }

    /// <summary>The PKCE S256 challenge for <paramref name="codeVerifier"/>: base64url (no padding) of its SHA-256.</summary>
    internal static string CodeChallenge(string codeVerifier) =>
        Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>Base64url encoding without padding (RFC 4648 §5), as PKCE and the OAuth state use.</summary>
    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// 32 random bytes, base64url-encoded (43 characters): used for the OAuth <c>state</c> and as
    /// the PKCE code verifier.
    /// </summary>
    internal static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>Exchanges the one-time <paramref name="code"/> and its PKCE <paramref name="codeVerifier"/> for the bearer token.</summary>
    private async Task<string> ExchangeCodeForTokenAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_botHost, "/api/companion/oauth/token");
        using var response = await _httpClient
            .PostAsJsonAsync(requestUri, new { code, code_verifier = codeVerifier }, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string message = TryExtractError(body) ?? $"Token exchange failed with status {(int)response.StatusCode}.";
            throw new CompanionAuthException(message, (int)response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("token", out JsonElement tokenElement))
        {
            throw new CompanionAuthException("Token exchange response did not contain a token.");
        }

        return tokenElement.GetString() ?? throw new CompanionAuthException("Token exchange returned an empty token.");
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement errorElement))
            {
                return errorElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the generic message.
        }

        return null;
    }
}
