using BCUKCompanion.Core.Models;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Matches a redemption (by reward title) or advanced command (by command name) event to its configured actions and runs them in order, reporting a
/// result per action. Core has no knowledge of what any individual <see cref="IEventAction"/>
/// does — apps contribute their own kinds (see <see cref="EventActionTypeRegistry"/>) and supply
/// whatever those kinds need via <see cref="IEventActionContext"/>.
/// </summary>
public sealed class EventActionDispatcher(
    Func<IReadOnlyList<EventActionMapping>> mappingsProvider,
    Func<IEventActionContext> contextProvider)
{
    // Core contract values from BCUKCompanion.Core.Models.BotEventArgs, not arbitrary choices.
    /// <summary>The event name of a channel-point redemption; its mappings match on the <c>rewardTitle</c> metadata.</summary>
    public const string RedemptionEventName = "redemption.received";
    /// <summary>The event name of an advanced command; its mappings match on the <c>commandName</c> metadata.</summary>
    public const string CommandEventName = "command.triggered";
    private const string RewardTitleMetadataKey = "rewardTitle";
    private const string CommandNameMetadataKey = "commandName";

    /// <summary>
    /// Runs the actions mapped to a redemption (by its <c>rewardTitle</c>) or an advanced command
    /// (by its <c>commandName</c>). The event itself is available to the actions as
    /// <c>context.GetService&lt;BotEventArgs&gt;()</c>.
    /// </summary>
    /// <param name="botEvent">The event received from the bot; its <c>rewardTitle</c> or <c>commandName</c> metadata picks the mappings.</param>
    /// <param name="cancellationToken">Passed to each action; an action that observes it stops the run by throwing, and that cancellation propagates. The dispatcher doesn't check it between actions.</param>
    /// <returns>The per-action results, or null if the event isn't one mappings apply to.</returns>
    public async Task<EventDispatchResult?> DispatchAsync(BotEventArgs botEvent, CancellationToken cancellationToken = default)
    {
        var (kind, key) = botEvent.EventName switch
        {
            RedemptionEventName => (EventActionTriggerKind.Reward, RewardTitleMetadataKey),
            CommandEventName => (EventActionTriggerKind.Command, CommandNameMetadataKey),
            _ => (EventActionTriggerKind.Reward, null),
        };
        if (key is null || !botEvent.Metadata.TryGetValue(key, out var name) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return await DispatchAsync(name, kind, botEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the actions mapped to a reward title (e.g. a settings window's Test button).</summary>
    /// <param name="rewardTitle">The reward title to match, case-insensitively, against reward mappings.</param>
    /// <param name="cancellationToken">Passed to each action; an action that observes it stops the run by throwing, and that cancellation propagates. The dispatcher doesn't check it between actions.</param>
    /// <returns>A task whose result holds one result per action run; empty when nothing is mapped to the title.</returns>
    public Task<EventDispatchResult> DispatchAsync(string rewardTitle, CancellationToken cancellationToken = default)
        => DispatchAsync(rewardTitle, EventActionTriggerKind.Reward, null, cancellationToken);

    /// <summary>Runs the actions mapped to a reward title or command name.</summary>
    /// <param name="name">The reward title or command name, matched case-insensitively.</param>
    /// <param name="kind">Which kind of mapping to match.</param>
    /// <param name="botEvent">The event being dispatched, exposed to actions; null for a test run.</param>
    /// <param name="cancellationToken">Passed to each action; an action that observes it stops the run by throwing, and that cancellation propagates. The dispatcher doesn't check it between actions.</param>
    /// <returns>A task whose result holds one result per action run, in order; empty when nothing matches.</returns>
    public async Task<EventDispatchResult> DispatchAsync(
        string name, EventActionTriggerKind kind, BotEventArgs? botEvent = null, CancellationToken cancellationToken = default)
    {
        var mappings = mappingsProvider();

        var actions = mappings
            .Where(m => m.TriggerKind == kind && string.Equals(m.RewardTitle, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.Actions)
            .ToList();

        if (actions.Count == 0)
        {
            return new EventDispatchResult(name, []);
        }

        var context = botEvent is null ? contextProvider() : new BotEventActionContext(botEvent, contextProvider());

        // Sequential, not parallel: a Delay action only delays the actions queued after it.
        var results = new List<EventActionResult>(actions.Count);
        foreach (var action in actions)
        {
            results.Add(await ExecuteActionAsync(action, context, cancellationToken).ConfigureAwait(false));
        }

        return new EventDispatchResult(name, results);
    }

    /// <summary>Validates and runs one action, turning a validation failure or an exception into a failed result. Only the caller's own cancellation propagates.</summary>
    /// <param name="action">The action to validate and run.</param>
    /// <param name="context">The services the action can ask for.</param>
    /// <param name="cancellationToken">The caller's cancellation; its cancellation is rethrown rather than reported as a failure.</param>
    /// <returns>A task whose result says whether the action succeeded and, if not, why.</returns>
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
