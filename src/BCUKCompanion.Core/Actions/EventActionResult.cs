namespace BCUKCompanion.Core.Actions;

public sealed record EventActionResult(IEventAction Action, bool Success, string? ErrorMessage);

public sealed record EventDispatchResult(string RewardTitle, IReadOnlyList<EventActionResult> ActionResults)
{
    public bool AllSucceeded => ActionResults.Count > 0 && ActionResults.All(r => r.Success);

    /// <summary>
    /// The failed actions' errors joined with "; ", or null when no action failed (including when
    /// none ran). Shared by the tray balloon and the advanced-command acknowledgement so both
    /// describe a failure the same way.
    /// </summary>
    public string? FailureSummary => ActionResults.Any(r => !r.Success)
        ? string.Join("; ", ActionResults.Where(r => !r.Success).Select(r => r.ErrorMessage ?? "Action failed."))
        : null;
}
