using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Actions;

/// <summary>What a mapping's name matches: a channel-point reward's title, or an advanced command's name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventActionTriggerKind
{
    /// <summary>Matches a <c>redemption.received</c> event by reward title.</summary>
    Reward,

    /// <summary>Matches a <c>command.triggered</c> event by advanced command name.</summary>
    Command,
}

/// <summary>
/// Runs <paramref name="Actions"/> when an event of <paramref name="TriggerKind"/> named
/// <paramref name="RewardTitle"/> arrives. The name keeps its original <c>RewardTitle</c> property
/// (and JSON key) so configs saved before command mappings existed load unchanged — a missing
/// <c>TriggerKind</c> means <see cref="EventActionTriggerKind.Reward"/>.
/// </summary>
/// <param name="RewardTitle">The reward title or command name to match, case-insensitively.</param>
/// <param name="Actions">The actions to run, in order.</param>
/// <param name="TriggerKind">Whether <paramref name="RewardTitle"/> names a reward or a command.</param>
public sealed record EventActionMapping(
    string RewardTitle,
    IReadOnlyList<IEventAction> Actions,
    EventActionTriggerKind TriggerKind = EventActionTriggerKind.Reward)
{
    public override string ToString() => TriggerKind == EventActionTriggerKind.Command
        ? $"Command: {RewardTitle} ({Actions.Count} action(s))"
        : $"{RewardTitle} ({Actions.Count} action(s))";
}
