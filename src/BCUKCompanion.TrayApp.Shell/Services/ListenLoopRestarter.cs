using BCUKCompanion.Core.Events;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Restarts the companion client's listen loop after it faults. The first fault restarts it
/// straight away; faults that keep coming back within <see cref="QuietPeriod"/> of each other
/// wait longer each time (<see cref="ReconnectBackoff"/>), so a fault that repeats on every
/// connect can't spin the loop and flood the tray with balloons.
/// </summary>
internal sealed class ListenLoopRestarter
{
    /// <summary>How long without a fault before the next one counts as the first again and restarts at once.</summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider;
    private int _consecutiveFaults;
    private long? _lastFaultAt;

    /// <summary>Creates a restarter that measures delays and quiet periods with <paramref name="timeProvider"/>.</summary>
    /// <param name="timeProvider">The clock the delays and <see cref="QuietPeriod"/> are measured with.</param>
    public ListenLoopRestarter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Records a fault and waits as long as the faults so far call for, then runs <paramref name="restart"/>.</summary>
    /// <param name="restart">Starts listening again. Runs on the caller's synchronization context.</param>
    /// <param name="onFailure">Told about an exception <paramref name="restart"/> throws, which nothing else would observe.</param>
    /// <returns>A task that completes once <paramref name="restart"/> has run; it never faults.</returns>
    public async Task RestartAfterFaultAsync(Action restart, Action<Exception> onFailure)
    {
        TimeSpan delay = NextDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider).ConfigureAwait(true);
        }

        try
        {
            restart();
        }
        catch (Exception ex)
        {
            onFailure(ex);
        }
    }

    /// <summary>Records a fault now and works out how long to wait before restarting after it.</summary>
    /// <returns>Zero for a first fault (or one after a quiet period), otherwise the backoff delay for the faults in a row so far.</returns>
    internal TimeSpan NextDelay()
    {
        long now = _timeProvider.GetTimestamp();
        bool repeated = _lastFaultAt is { } last && _timeProvider.GetElapsedTime(last, now) < QuietPeriod;
        _consecutiveFaults = repeated ? _consecutiveFaults + 1 : 0;
        _lastFaultAt = now;
        return _consecutiveFaults == 0 ? TimeSpan.Zero : ReconnectBackoff.GetDelay(_consecutiveFaults - 1);
    }
}
