namespace BCUKCompanion.Core.Actions;

public sealed record EventActionMapping(string RewardTitle, List<IEventAction> Actions)
{
    public override string ToString() => $"{RewardTitle} ({Actions.Count} action(s))";
}
