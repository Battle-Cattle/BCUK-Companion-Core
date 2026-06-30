namespace BCUKCompanion.Core.Actions;

public sealed record EventActionMapping(string RewardTitle, IReadOnlyList<IEventAction> Actions)
{
    public override string ToString() => $"{RewardTitle} ({Actions.Count} action(s))";
}
