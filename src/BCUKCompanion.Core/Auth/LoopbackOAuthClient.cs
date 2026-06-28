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
        using var listener = new HttpListener();
        int port = StartListenerOnFreePort(listener);
        var redirectUri = new Uri($"http://127.0.0.1:{port}/callback");

        try
        {
            Uri loginUrl = BuildLoginUrl(redirectUri, state);
            await openBrowser(loginUrl).ConfigureAwait(false);

            (HttpListenerContext context, string code) = await WaitForCallbackAsync(
                listener, state, timeout ?? TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            try
            {
                // Don't tell the browser login succeeded until the code has actually
                // been exchanged for a token — otherwise a later exchange failure
                // leaves the user seeing a false "success" page.
                string token = await ExchangeCodeForTokenAsync(code, cancellationToken).ConfigureAwait(false);
                RespondHtml(context, 200, "<html><body>Login successful — you can close this window and return to the app.</body></html>");
                return token;
            }
            catch (Exception)
            {
                RespondHtml(context, 502, "<html><body>Login failed: the app could not complete sign-in. You can close this window.</body></html>");
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

    /// <summary>
    /// Probes for a free loopback port and starts <paramref name="listener"/> on it,
    /// retrying with a new port if another process wins the race for the one just probed.
    /// </summary>
    private static int StartListenerOnFreePort(HttpListener listener)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            int port = GetFreeLoopbackPort();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/callback/");
            try
            {
                listener.Start();
                return port;
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                // Another process grabbed the port between the probe and the bind — retry with a new one.
            }
        }

        throw new CompanionAuthException("Could not bind a local port for the companion login callback.");
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(HttpListenerContext Context, string Code)> WaitForCallbackAsync(
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

        string? code = context.Request.QueryString["code"];
        string? returnedState = context.Request.QueryString["state"];

        if (string.IsNullOrEmpty(code) || returnedState != expectedState)
        {
            RespondHtml(context, 400, "<html><body>Login failed: invalid response from server.</body></html>");
            context.Response.OutputStream.Close();
            throw new CompanionAuthException("Loopback callback returned a missing code or mismatched state.");
        }

        return (context, code);
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
