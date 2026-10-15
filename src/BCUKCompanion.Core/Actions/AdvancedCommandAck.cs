namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Turns the result of dispatching an advanced command into the acknowledgement sent back to the
/// bot (see <see cref="CompanionClient.AckAdvancedCommandAsync"/>). Anything short of every mapped
/// action succeeding is a failure, so the viewer is refunded — including no mapping at all, since
/// nothing the viewer paid for happened.
/// </summary>
public static class AdvancedCommandAck
{
    /// <summary>Error sent when no mapping matched the command.</summary>
    public const string NoMappingError = "No actions are mapped to this command.";

    /// <summary>
    /// Builds the acknowledgement for a dispatch result.
    /// </summary>
    /// <param name="result">The dispatch result, or null if nothing handled the event.</param>
    /// <returns>Whether to acknowledge success, and the error text otherwise.</returns>
    public static (bool Ok, string? Error) FromDispatchResult(EventDispatchResult? result)
    {
        if (result is null || result.ActionResults.Count == 0)
        {
            return (false, NoMappingError);
        }

        if (result.AllSucceeded)
        {
            return (true, null);
        }

        var errors = result.ActionResults.Where(r => !r.Success).Select(r => r.ErrorMessage ?? "Action failed.");
        return (false, string.Join("; ", errors));
    }
}
