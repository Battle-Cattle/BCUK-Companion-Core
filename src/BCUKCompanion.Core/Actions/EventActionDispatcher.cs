using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Matches a redemption event to its configured actions and runs them in order, reporting a
/// result per action. Core has no knowledge of what any individual <see cref="IEventAction"/>
/// does — apps contribute their own kinds (see <see cref="EventActionTypeRegistry"/>) and supply
/// whatever those kinds need via <see cref="IEventActionContext"/>.
/// </summary>
public sealed class EventActionDispatcher(
    Func<IReadOnlyList<EventActionMapping>> mappingsProvider,
    Func<IEventActionContext> contextProvider)
{
    // Core contract values from BCUKCompanion.Core.Models.BotEventArgs, not arbitrary choices.
    private const string RedemptionEventName = "redemption.received";
    private const string RewardTitleMetadataKey = "rewardTitle";

    public async Task<EventDispatchResult?> DispatchAsync(BotEventArgs botEvent, CancellationToken cancellationToken = default)
    {
        if (botEvent.EventName != RedemptionEventName
            || !botEvent.Metadata.TryGetValue(RewardTitleMetadataKey, out var rewardTitle)
            || string.IsNullOrEmpty(rewardTitle))
        {
            return null;
        }

        return await DispatchAsync(rewardTitle, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventDispatchResult> DispatchAsync(string rewardTitle, CancellationToken cancellationToken = default)
    {
        var mappings = mappingsProvider();

        var actions = mappings
            .Where(m => string.Equals(m.RewardTitle, rewardTitle, StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.Actions)
            .ToList();

        if (actions.Count == 0)
        {
            return new EventDispatchResult(rewardTitle, []);
        }

        var context = contextProvider();

        // Sequential, not parallel: a Delay action only delays the actions queued after it.
        var results = new List<EventActionResult>(actions.Count);
        foreach (var action in actions)
        {
            results.Add(await ExecuteActionAsync(action, context, cancellationToken).ConfigureAwait(false));
        }

        return new EventDispatchResult(rewardTitle, results);
    }

    private static async Task<EventActionResult> ExecuteActionAsync(
        IEventAction action, IEventActionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var errors = action.Validate(context);
            if (errors.Count > 0)
            {
                return new EventActionResult(action, false, string.Join("; ", errors));
            }

            var success = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            return new EventActionResult(action, success, success ? null : "Action did not complete successfully.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EventActionResult(action, false, ex.Message);
        }
    }
}
