using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>Built-in action that waits before any actions queued after it run.</summary>
public sealed class DelayAction : IEventAction
{
    /// <summary>The <c>kind</c> this action is stored under in action configs.</summary>
    public const string ActionKind = "delay";
    /// <summary>The shortest delay allowed, in seconds.</summary>
    public const int MinDelaySeconds = 1;
    /// <summary>The longest delay allowed, in seconds (an hour).</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>How long to wait, in seconds (<see cref="MinDelaySeconds"/>–<see cref="MaxDelaySeconds"/>).</summary>
    public int DelaySeconds { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string Kind => ActionKind;

    /// <summary>Describes the action for the mappings list, e.g. "Delay 5s".</summary>
    /// <param name="context">Unused: the description needs no live state.</param>
    public string Describe(IEventActionContext context) => $"Delay {DelaySeconds}s";

    /// <summary>Rejects a delay outside <see cref="MinDelaySeconds"/>–<see cref="MaxDelaySeconds"/>.</summary>
    /// <param name="context">Unused: the check needs no live state.</param>
    public IReadOnlyList<string> Validate(IEventActionContext context)
    {
        if (DelaySeconds < MinDelaySeconds || DelaySeconds > MaxDelaySeconds)
        {
            return [$"Delay must be between {MinDelaySeconds} and {MaxDelaySeconds} seconds."];
        }

        return [];
    }

    /// <summary>Waits <see cref="DelaySeconds"/> seconds.</summary>
    /// <param name="context">Unused.</param>
    /// <param name="cancellationToken">Ends the wait early, by throwing <see cref="OperationCanceledException"/>.</param>
    /// <returns>True once the wait completes.</returns>
    public async Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(DelaySeconds), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
