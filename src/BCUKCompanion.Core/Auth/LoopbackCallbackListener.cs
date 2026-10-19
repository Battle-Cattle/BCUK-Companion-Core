using System.Net;

namespace BCUKCompanion.Core.Auth;

/// <summary>
/// The local HTTP side of <see cref="LoopbackOAuthClient"/>'s login: binding a loopback port for
/// the bot's redirect, waiting for that redirect, and answering the browser.
/// </summary>
internal static class LoopbackCallbackListener
{
    /// <summary>
    /// Probes for a free loopback port and starts <paramref name="listener"/> on it,
    /// retrying with a new port if another process wins the race for the one just probed.
    /// </summary>
    internal static int StartListenerOnFreePort(HttpListener listener)
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

    /// <summary>A loopback port that was free a moment ago (another process can still take it before it is bound).</summary>
    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Waits for the bot's redirect back to the loopback listener. A request without a code or
    /// with the wrong <c>state</c> (a browser retry, or another local process probing the port) is
    /// answered with 400 and ignored, rather than failing the login. Otherwise only an
    /// <c>error</c> redirect carrying the expected state, <paramref name="timeout"/> or the
    /// caller's cancellation ends the wait.
    /// </summary>
    /// <exception cref="CompanionAuthException">The bot redirected back with an <c>error</c>.</exception>
    internal static async Task<(HttpListenerContext Context, string Code)> WaitForCallbackAsync(
        HttpListener listener, string expectedState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var cancelTcs = new TaskCompletionSource();
        using CancellationTokenRegistration registration = timeoutCts.Token.Register(() =>
        {
            cancelTcs.TrySetResult();
        });

        while (true)
        {
            HttpListenerContext context = await NextContextAsync(listener, cancelTcs.Task, cancellationToken)
                .ConfigureAwait(false);
            if (TryGetCallbackCode(context, expectedState, out string code))
            {
                return (context, code);
            }

            if (GetCallbackError(context, expectedState) is { } error)
            {
                // A genuine failure (e.g. consent denied): fail now rather than wait out the timeout.
                RespondAndClose(context, 400, "<html><body>Login failed. You can close this window and try again.</body></html>");
                throw new CompanionAuthException($"Login failed: {error}");
            }

            RespondAndClose(context, 400, "<html><body>Login failed: invalid response from server.</body></html>");
        }
    }

    /// <summary>Answers the browser and closes the response, tolerating a client that already went away.</summary>
    internal static void RespondAndClose(HttpListenerContext context, int statusCode, string html)
    {
        try
        {
            RespondHtml(context, statusCode, html);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            context.Response.Abort();
        }
    }

    /// <summary>
    /// The next request to <paramref name="listener"/>, or an <see cref="OperationCanceledException"/>
    /// once <paramref name="canceled"/> completes (the caller's cancellation or the login timeout).
    /// </summary>
    private static async Task<HttpListenerContext> NextContextAsync(
        HttpListener listener, Task canceled, CancellationToken cancellationToken)
    {
        Task<HttpListenerContext> contextTask = listener.GetContextAsync();
        Task completed = await Task.WhenAny(contextTask, canceled).ConfigureAwait(false);
        if (completed == contextTask)
        {
            return await contextTask.ConfigureAwait(false);
        }

        // contextTask lost the race and is abandoned here. The caller's finally block
        // will call listener.Stop() shortly after, which typically faults the still-pending
        // GetContextAsync() (HttpListenerException/ObjectDisposedException). Nothing else
        // observes that task, so without this it would surface as an unobserved task
        // exception during finalization. Observe and discard the fault instead of rethrowing.
        _ = contextTask.ContinueWith(
            t => t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        // `canceled` fires both when the caller's own token is canceled and when the timeout
        // elapses. Check the caller's token specifically so a deliberate cancel isn't
        // misreported as a timeout.
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException("Timed out waiting for the companion login callback.");
    }

    /// <summary>Whether <paramref name="context"/> is the bot's redirect: a code and the expected state.</summary>
    private static bool TryGetCallbackCode(HttpListenerContext context, string expectedState, out string code)
    {
        code = context.Request.QueryString["code"] ?? string.Empty;
        return code.Length > 0 && context.Request.QueryString["state"] == expectedState;
    }

    /// <summary>The <c>error</c> of a redirect carrying the expected <c>state</c>, or null if it isn't one.</summary>
    private static string? GetCallbackError(HttpListenerContext context, string expectedState)
    {
        string? error = context.Request.QueryString["error"];
        return !string.IsNullOrEmpty(error) && context.Request.QueryString["state"] == expectedState ? error : null;
    }

    /// <summary>Writes <paramref name="html"/> as the response to the browser with <paramref name="statusCode"/>.</summary>
    private static void RespondHtml(HttpListenerContext context, int statusCode, string html)
    {
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
    }
}
