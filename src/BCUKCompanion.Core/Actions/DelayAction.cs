using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>Built-in action that waits before any actions queued after it run.</summary>
public sealed class DelayAction : IEventAction
{
    public const string ActionKind = "delay";
    public const int MinDelaySeconds = 1;
    public const int MaxDelaySeconds = 3600;

    public int DelaySeconds { get; set; }

    [JsonIgnore]
    public string Kind => ActionKind;

    public string Describe(IEventActionContext context) => $"Delay {DelaySeconds}s";

    public IReadOnlyList<string> Validate(IEventActionContext context)
    {
        if (DelaySeconds < MinDelaySeconds || DelaySeconds > MaxDelaySeconds)
        {
            return [$"Delay must be between {MinDelaySeconds} and {MaxDelaySeconds} seconds."];
        }

        return [];
    }

    public async Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(DelaySeconds), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
