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
    /// until it finishes. A failed ping is ignored (an older bot without progress pings answers 404;
    /// the next ping, or the acknowledgement, still goes out), and no ping is sent once the work is done.
    /// </summary>
    /// <param name="work">The event's actions; its result is returned.</param>
    /// <param name="ping">Sends one progress ping; canceled once the work finishes.</param>
    /// <param name="interval">Time between pings; <see cref="DefaultInterval"/> when null.</param>
    /// <returns>Whatever <paramref name="work"/> returns.</returns>
    public static async Task<T> RunWithProgressAsync<T>(Func<Task<T>> work, Func<CancellationToken, Task> ping, TimeSpan? interval = null)
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

    private static async Task PingUntilCanceledAsync(Func<CancellationToken, Task> ping, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await ping(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
