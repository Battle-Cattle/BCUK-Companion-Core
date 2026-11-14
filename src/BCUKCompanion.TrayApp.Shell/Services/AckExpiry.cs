using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Notices, from an acknowledgement-required event's progress pings, when the bot stops waiting
/// for it (it timed the event out and refunded the viewer, or no longer knows it), so an event
/// still queued behind others can be skipped instead of running after its refund.
/// </summary>
internal sealed class AckExpiry
{
    private volatile bool _expired;

    /// <summary>Whether the bot has said it's no longer waiting for this event.</summary>
    public bool Expired => _expired;

    /// <summary>Wraps a progress ping so that its answer also records whether the event has expired.</summary>
    /// <param name="ping">Sends one progress ping; true while the bot is still waiting for the event.</param>
    /// <returns>The same ping, which also sets <see cref="Expired"/> when the bot answers false or 404 (unknown event).</returns>
    public Func<CancellationToken, Task<bool>> Watch(Func<CancellationToken, Task<bool>> ping) => async cancellationToken =>
    {
        try
        {
            bool extended = await ping(cancellationToken).ConfigureAwait(false);
            if (!extended)
            {
                _expired = true;
            }

            return extended;
        }
        catch (CompanionApiException ex) when (ex.StatusCode == 404)
        {
            _expired = true;
            throw;
        }
    };
}
