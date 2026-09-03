namespace BCUKCompanion.Core.Events;

/// <summary>
/// Exponential reconnect backoff, capped at 30 seconds, per attempt number (0-based).
/// A randomized jitter of &#177;20% is applied to the computed delay before the cap so that many
/// companion-app instances reconnecting after a shared bot-host outage don't all retry in lockstep
/// (thundering herd) on the same cadence.
/// </summary>
public static class ReconnectBackoff
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);
    private const double JitterFraction = 0.2;

    /// <param name="attempt">0-based reconnect attempt number.</param>
    public static TimeSpan GetDelay(int attempt) => GetDelay(attempt, null);

    /// <param name="attempt">0-based reconnect attempt number.</param>
    /// <param name="random">
    /// Random source used for jitter. Defaults to <see cref="Random.Shared"/>; pass an explicit
    /// instance (e.g. seeded) for deterministic tests.
    /// </param>
    public static TimeSpan GetDelay(int attempt, Random? random)
    {
        if (attempt < 0)
        {
            attempt = 0;
        }

        random ??= Random.Shared;

        double baseSeconds = Math.Pow(2, attempt);
        double jitterMultiplier = 1.0 + ((random.NextDouble() * 2.0) - 1.0) * JitterFraction;
        double jitteredSeconds = baseSeconds * jitterMultiplier;

        double seconds = Math.Min(Cap.TotalSeconds, Math.Max(0, jitteredSeconds));
        return TimeSpan.FromSeconds(seconds);
    }
}
