namespace BCUKCompanion.Core.Actions;

/// <summary>The outcome of running one action.</summary>
/// <param name="Action">The action that ran.</param>
/// <param name="Success">Whether it passed validation and reported success.</param>
/// <param name="ErrorMessage">Why it failed (validation errors or the exception's message), or null on success.</param>
public sealed record EventActionResult(IEventAction Action, bool Success, string? ErrorMessage);

/// <summary>The outcome of dispatching one event to its mapping's actions.</summary>
/// <param name="RewardTitle">The mapping's reward title or command name.</param>
/// <param name="ActionResults">One result per action, in the order they ran.</param>
public sealed record EventDispatchResult(string RewardTitle, IReadOnlyList<EventActionResult> ActionResults)
{
    /// <summary>Whether at least one action ran and every action succeeded.</summary>
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
