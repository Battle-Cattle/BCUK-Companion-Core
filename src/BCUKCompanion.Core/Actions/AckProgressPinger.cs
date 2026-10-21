namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Keeps the bot from refunding an event whose actions are still running: while the work runs, a
/// progress ping (e.g. <see cref="CompanionClient.ReportCommandProgressAsync"/>) is sent every
/// <see cref="DefaultInterval"/>, each one restarting the bot's acknowledgement timeout. The bot
/// times out after 3 minutes, so a ping a minute leaves room for a slow or failed one.
/// </summary>
public static class AckProgressPinger
{
    /// <summary>How often a progress ping is sent while the work runs.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <paramref name="work"/>, sending <paramref name="ping"/> every <paramref name="interval"/>
    /// until it finishes. Pinging stops early once the bot says it's no longer waiting for the event
    /// (<paramref name="ping"/> returns false: it already timed out or refunded, or the event hit the
    /// bot's cap) or answers 404 (a bot that predates progress pings, or an event it doesn't know).
    /// Any other failed ping is ignored, so the next one still goes out, and no ping is sent once the
    /// work is done.
    /// </summary>
    /// <param name="work">The event's actions; its result is returned.</param>
    /// <param name="ping">Sends one progress ping and returns whether the bot extended its timeout; canceled once the work finishes.</param>
    /// <param name="interval">Time between pings; <see cref="DefaultInterval"/> when null.</param>
    /// <returns>Whatever <paramref name="work"/> returns.</returns>
    public static async Task<T> RunWithProgressAsync<T>(Func<Task<T>> work, Func<CancellationToken, Task<bool>> ping, TimeSpan? interval = null)
    {
        using var done = new CancellationTokenSource();
        Task pinging = PingUntilCanceledAsync(ping, interval ?? DefaultInterval, done.Token);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            done.Cancel();
            await pinging.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends <paramref name="ping"/> every <paramref name="interval"/> until <paramref name="cancellationToken"/>
    /// is canceled, the bot stops extending its timeout, or it answers 404; other failures are ignored.
    /// </summary>
    /// <param name="ping">Sends one progress ping and returns whether the bot extended its timeout.</param>
    /// <param name="interval">Time to wait before each ping.</param>
    /// <param name="cancellationToken">Canceled once the work finishes, which stops pinging.</param>
    /// <returns>A task that completes when pinging stops; it never faults.</returns>
    private static async Task PingUntilCanceledAsync(Func<CancellationToken, Task<bool>> ping, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (!await ping(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (CompanionApiException ex) when (ex.StatusCode == 404)
            {
                return;
            }
            catch (Exception)
            {
                // Best-effort: the bot's timeout still covers an event whose pings never arrive.
            }
        }
    }
}
