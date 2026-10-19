namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Turns the result of dispatching an advanced command, or a redemption whose reward requires
/// companion confirmation, into the acknowledgement sent back to the bot (see
/// <see cref="CompanionClient.AckAdvancedCommandAsync"/> and
/// <see cref="CompanionClient.AckRedemptionAsync"/>). Anything short of every mapped action
/// succeeding is a failure, so the viewer is refunded — including no mapping at all, since
/// nothing the viewer paid for happened.
/// </summary>
public static class AdvancedCommandAck
{
    /// <summary>Error sent when no mapping matched the command or reward.</summary>
    public const string NoMappingError = "No actions are mapped to this event.";

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

        return (false, result.FailureSummary);
    }

    /// <summary>
    /// Combines several handlers' acknowledgements for one event (e.g. a host's own handler and the
    /// mapped actions): success only if every one succeeded, otherwise a failure carrying every
    /// failing handler's error, in order.
    /// </summary>
    /// <param name="acks">Each handler's acknowledgement.</param>
    /// <returns>The acknowledgement to send.</returns>
    public static (bool Ok, string? Error) Combine(params (bool Ok, string? Error)[] acks)
    {
        string[] errors = acks.Where(a => !a.Ok).Select(a => a.Error ?? "Failed.").ToArray();
        return errors.Length == 0 ? (true, null) : (false, string.Join("; ", errors));
    }
}
