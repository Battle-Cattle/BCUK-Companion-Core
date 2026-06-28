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
/// a long-lived bearer token.
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
        int port = GetFreeLoopbackPort();
        var redirectUri = new Uri($"http://127.0.0.1:{port}/callback");

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/callback/");
        listener.Start();

        try
        {
            Uri loginUrl = BuildLoginUrl(redirectUri, state);
            await openBrowser(loginUrl).ConfigureAwait(false);

            string code = await WaitForCallbackAsync(
                listener, state, timeout ?? TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            return await ExchangeCodeForTokenAsync(code, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    internal Uri BuildLoginUrl(Uri redirectUri, string state)
    {
        string query = $"redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}&state={Uri.EscapeDataString(state)}";
        return new Uri(_botHost, $"/companion/login?{query}");
    }

    internal static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> WaitForCallbackAsync(
        HttpListener listener, string expectedState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        Task<HttpListenerContext> contextTask = listener.GetContextAsync();
        var cancelTcs = new TaskCompletionSource();
        using CancellationTokenRegistration registration = timeoutCts.Token.Register(() =>
        {
            cancelTcs.TrySetResult();
        });

        Task completed = await Task.WhenAny(contextTask, cancelTcs.Task).ConfigureAwait(false);
        if (completed != contextTask)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            throw new OperationCanceledException("Timed out waiting for the companion login callback.");
        }

        HttpListenerContext context = await contextTask.ConfigureAwait(false);
        try
        {
            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];

            if (string.IsNullOrEmpty(code) || returnedState != expectedState)
            {
                RespondHtml(context, 400, "<html><body>Login failed: invalid response from server.</body></html>");
                throw new CompanionAuthException("Loopback callback returned a missing code or mismatched state.");
            }

            RespondHtml(context, 200, "<html><body>Login successful — you can close this window and return to the app.</body></html>");
            return code;
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }

    private static void RespondHtml(HttpListenerContext context, int statusCode, string html)
    {
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
    }

    private async Task<string> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_botHost, "/api/companion/oauth/token");
        using var response = await _httpClient
            .PostAsJsonAsync(requestUri, new { code }, cancellationToken)
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
