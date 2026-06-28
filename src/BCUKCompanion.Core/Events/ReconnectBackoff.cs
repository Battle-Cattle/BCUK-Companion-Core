namespace BCUKCompanion.Core.Events;

/// <summary>Exponential reconnect backoff, capped at 30 seconds, per attempt number (0-based).</summary>
public static class ReconnectBackoff
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    public static TimeSpan GetDelay(int attempt)
    {
        if (attempt < 0)
        {
            attempt = 0;
        }

        double seconds = Math.Min(Cap.TotalSeconds, Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds);
    }
}
