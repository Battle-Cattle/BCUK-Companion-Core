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
    public const string RedemptionEventName = "redemption.received";
    public const string CommandEventName = "command.triggered";
    private const string RewardTitleMetadataKey = "rewardTitle";
    private const string CommandNameMetadataKey = "commandName";

    /// <summary>
    /// Runs the actions mapped to a redemption (by its <c>rewardTitle</c>) or an advanced command
    /// (by its <c>commandName</c>). The event itself is available to the actions as
    /// <c>context.GetService&lt;BotEventArgs&gt;()</c>.
    /// </summary>
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
    public Task<EventDispatchResult> DispatchAsync(string rewardTitle, CancellationToken cancellationToken = default)
        => DispatchAsync(rewardTitle, EventActionTriggerKind.Reward, null, cancellationToken);

    /// <summary>Runs the actions mapped to a reward title or command name.</summary>
    /// <param name="name">The reward title or command name, matched case-insensitively.</param>
    /// <param name="kind">Which kind of mapping to match.</param>
    /// <param name="botEvent">The event being dispatched, exposed to actions; null for a test run.</param>
    /// <param name="cancellationToken">Cancels the remaining actions.</param>
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
