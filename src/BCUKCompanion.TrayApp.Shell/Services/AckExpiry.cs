using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Works out, from an acknowledgement-required event's progress pings, whether the bot may have
/// stopped waiting for it (timed it out and refunded the viewer), so an event still queued
/// behind others can be skipped instead of running after its refund. It errs towards "expired":
/// the event counts as expired once the bot answers a ping with "not extended", or once no ping
/// has been confirmed for <see cref="UnconfirmedLimit"/>, which covers pings that fail and bots
/// that predate progress pings (they answer 404 and never extend the timeout).
/// </summary>
internal sealed class AckExpiry
{
    /// <summary>
    /// How long the event may go without a confirmed ping before it counts as expired: shorter
    /// than the bot's 3-minute acknowledgement timeout, to allow for the time the event took to
    /// arrive. Pings go out every minute, so this is only reached when they aren't getting through.
    /// </summary>
    internal static readonly TimeSpan UnconfirmedLimit = TimeSpan.FromSeconds(150);

    private readonly TimeProvider _timeProvider;
    private long _confirmedAt;
    private volatile bool _rejected;

    /// <summary>Starts tracking an event that has just arrived, which counts as confirmed now.</summary>
    /// <param name="timeProvider">The clock <see cref="UnconfirmedLimit"/> is measured with.</param>
    public AckExpiry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _confirmedAt = timeProvider.GetTimestamp();
    }

    /// <summary>Whether the bot may no longer be waiting for this event.</summary>
    public bool Expired =>
        _rejected || _timeProvider.GetElapsedTime(Interlocked.Read(ref _confirmedAt)) >= UnconfirmedLimit;

    /// <summary>Wraps a progress ping so that its answer also feeds <see cref="Expired"/>.</summary>
    /// <param name="ping">Sends one progress ping; true while the bot is still waiting for the event.</param>
    /// <returns>The same ping, which records a confirmation (as of when it was sent) when the bot extends the timeout, and a rejection when it answers false.</returns>
    public Func<CancellationToken, Task<bool>> Watch(Func<CancellationToken, Task<bool>> ping) => async cancellationToken =>
    {
        // Failures (including a 404 from a bot without progress pings) record nothing: the event
        // then expires once UnconfirmedLimit passes, as the bot's own timeout would.
        // The bot restarts its timeout when the ping reaches it, so count from when it was sent:
        // a slow response must not make the event look confirmed later than it was.
        long sentAt = _timeProvider.GetTimestamp();
        bool extended = await ping(cancellationToken).ConfigureAwait(false);
        if (extended)
        {
            Interlocked.Exchange(ref _confirmedAt, sentAt);
        }
        else
        {
            _rejected = true;
        }

        return extended;
    };
}
