using System.Diagnostics;
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

    /// <summary>
    /// Fire-and-forget: runs <see cref="DispatchAsync(BotEventArgs, CancellationToken)"/> on a
    /// background thread and reports failures via <paramref name="showBalloon"/> — a dispatch
    /// that throws (a crash) and one that completed but had unsuccessful actions (a wrong
    /// device IP, an offline light, a disconnected treadmill, ...) alike — so they're visible
    /// even in a Release build, where <see cref="Debug.WriteLine"/> alone is compiled out.
    /// A null result (not a redemption event, or no mappings matched it) and a result with
    /// zero matching actions are not reported.
    /// </summary>
    public void DispatchAndReportAsync(BotEventArgs botEvent, Action<string, string>? showBalloon)
    {
        var dispatch = Task.Run(() => DispatchAsync(botEvent));

        dispatch.ContinueWith(
            t => Debug.WriteLine($"Action dispatch failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
        dispatch.ContinueWith(
            t => showBalloon?.Invoke("Action dispatch crashed", DescribeFault(t.Exception)),
            TaskContinuationOptions.OnlyOnFaulted);
        dispatch.ContinueWith(
            t => ReportDispatchFailure(t.Result, "Action failed", showBalloon),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private static void ReportDispatchFailure(
        EventDispatchResult? result, string balloonTitle, Action<string, string>? showBalloon)
    {
        if (result is null || result.ActionResults.Count == 0 || result.AllSucceeded)
        {
            return;
        }

        var detail = string.Join(
            "; ",
            result.ActionResults.Where(r => !r.Success).Select(r => r.ErrorMessage ?? "Action failed."));
        showBalloon?.Invoke(balloonTitle, detail);
    }

    private static string DescribeFault(AggregateException? exception) =>
        exception?.Flatten().InnerException?.Message ?? "Unknown error.";

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
