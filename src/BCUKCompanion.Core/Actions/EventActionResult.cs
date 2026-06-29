namespace BCUKCompanion.Core.Actions;

public sealed record EventActionResult(IEventAction Action, bool Success, string? ErrorMessage);

public sealed record EventDispatchResult(string RewardTitle, IReadOnlyList<EventActionResult> ActionResults)
{
    public bool AllSucceeded => ActionResults.Count > 0 && ActionResults.All(r => r.Success);
}
