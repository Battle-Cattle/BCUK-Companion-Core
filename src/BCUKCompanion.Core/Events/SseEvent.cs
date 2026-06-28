namespace BCUKCompanion.Core.Events;

/// <summary>One parsed Server-Sent Event block (excluding comment-only keepalive lines).</summary>
public sealed class SseEvent
{
    public string? EventName { get; init; }

    public string Data { get; init; } = string.Empty;
}
