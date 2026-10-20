namespace BCUKCompanion.Core.Events;

/// <summary>One parsed Server-Sent Event block (excluding comment-only keepalive lines).</summary>
public sealed class SseEvent
{
    /// <summary>The block's <c>event:</c> field, or null if it had none.</summary>
    public string? EventName { get; init; }

    /// <summary>The block's <c>data:</c> lines, joined with newlines.</summary>
    public string Data { get; init; } = string.Empty;
}
