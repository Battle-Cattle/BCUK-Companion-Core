using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Runs each bot event (redemption, activity or advanced command) the tray app receives: a
/// balloon, the host's handlers, the event's mapped actions, and the acknowledgement the bot is
/// waiting for. Event handlers are queued on the UI thread through the delegate the tray app
/// passes in, which keeps this class free of WPF so it can be tested on its own.
/// </summary>
internal sealed class BotEventHandler
{
    private readonly CompanionTrayAppOptions _options;
    private readonly Func<Func<Task>, Task> _runOnUiThread;
    private readonly Action<string, string> _showBalloon;

    /// <summary>Creates the handler for a tray app with <paramref name="options"/>; balloons go through <paramref name="showBalloon"/>.</summary>
    /// <param name="options">The host app's handlers and action dispatcher.</param>
    /// <param name="runOnUiThread">
    /// Queues work on the UI thread, in order, and returns a task for it (the tray app passes its
    /// dispatcher's <c>InvokeAsync</c>). Its tasks are never awaited: each handler catches its own errors.
    /// </param>
    /// <param name="showBalloon">Shows a tray balloon (title, text).</param>
    public BotEventHandler(CompanionTrayAppOptions options, Func<Func<Task>, Task> runOnUiThread, Action<string, string> showBalloon)
    {
        _options = options;
        _runOnUiThread = runOnUiThread;
        _showBalloon = showBalloon;
    }

    private static readonly Dictionary<string, string> ActivityTitles = new(StringComparer.Ordinal)
    {
        ["follow"] = "New Follower",
        ["sub"] = "New Sub",
        ["resub"] = "Resub",
        ["giftsub"] = "Gift Sub",
        ["raid"] = "Raid",
    };

    /// <summary>
    /// Shows a balloon for a redemption and hands it to the host's handlers and mapped actions,
    /// then acknowledges it to the bot when its reward requires companion confirmation — a
    /// failure there cancels the redemption and refunds the viewer. While those actions run, the
    /// bot gets a progress ping every minute so long actions aren't refunded mid-run.
    /// </summary>
    public void OnRedemption(CompanionClient client, RedemptionEvent redemption)
    {
        _ = _runOnUiThread(async () =>
        {
            _showBalloon(redemption.RewardTitle, $"Redeemed by {redemption.DisplayName}");

            var botEvent = new BotEventArgs(
                EventActionDispatcher.RedemptionEventName,
                new Dictionary<string, string?>
                {
                    ["redemptionId"] = redemption.Id,
                    ["rewardId"] = redemption.RewardId,
                    ["rewardTitle"] = redemption.RewardTitle,
                    ["userLogin"] = redemption.UserLogin,
                    ["userName"] = redemption.UserName,
                    ["userInput"] = redemption.UserInput,
                    ["redeemedAt"] = redemption.RedeemedAt.ToString("o"),
                });
            if (!redemption.NeedsAck)
            {
                await HandleBotEventAsync(botEvent).ConfigureAwait(true);
                return;
            }

            (bool ok, string? error) = await AckProgressPinger.RunWithProgressAsync(
                () => HandleBotEventAsync(botEvent),
                ct => client.ReportRedemptionProgressAsync(redemption.Id, ct)).ConfigureAwait(true);
            await TrySendAckAsync(() => client.AckRedemptionAsync(redemption.Id, ok, error)).ConfigureAwait(true);
        });
    }

    /// <summary>
    /// Shows a balloon for a follow/sub/raid/... and hands it to the host's handlers. Queued on the
    /// UI thread ahead of the tray app's save of its activity id, so the id is only saved once
    /// the activity has been handled.
    /// </summary>
    public void OnActivity(CompanionActivityEvent activity)
    {
        _ = _runOnUiThread(async () =>
        {
            string title = ActivityTitles.TryGetValue(activity.Type, out string? knownTitle) ? knownTitle : activity.Type;
            string text = string.IsNullOrEmpty(activity.Detail)
                ? activity.DisplayName
                : $"{activity.DisplayName} — {activity.Detail}";
            _showBalloon(title, text);

            await HandleBotEventAsync(new BotEventArgs(
                "activity.received",
                new Dictionary<string, string?>
                {
                    ["type"] = activity.Type,
                    ["displayName"] = activity.DisplayName,
                    ["detail"] = activity.Detail,
                    ["occurredAt"] = activity.OccurredAt.ToString("o"),
                })).ConfigureAwait(true);
        });
    }

    /// <summary>
    /// Shows a balloon for an advanced command, runs it through <see cref="HandleBotEventAsync"/>
    /// like any other event, then acknowledges it to the bot when it asks for an acknowledgement —
    /// a failure there refunds the viewer. While its actions run, the bot gets a progress ping every
    /// minute so long actions aren't refunded mid-run.
    /// </summary>
    public void OnAdvancedCommand(CompanionClient client, AdvancedCommandEvent command)
    {
        _ = _runOnUiThread(async () =>
        {
            string text = command.Source == "streamdeck"
                ? "Triggered from Stream Deck"
                : $"Used by {(string.IsNullOrEmpty(command.UserName) ? command.UserLogin : command.UserName)}";
            _showBalloon(command.CommandName, text);

            BotEventArgs botEvent = BuildCommandEventArgs(command);
            if (!command.AckRequired)
            {
                await HandleBotEventAsync(botEvent).ConfigureAwait(true);
                return;
            }

            (bool ok, string? error) = await AckProgressPinger.RunWithProgressAsync(
                () => HandleBotEventAsync(botEvent),
                ct => client.ReportCommandProgressAsync(command.Id, ct)).ConfigureAwait(true);
            await TrySendAckAsync(() => client.AckAdvancedCommandAsync(command.Id, ok, error)).ConfigureAwait(true);
        });
    }

    /// <summary>
    /// Sends an acknowledgement to the bot, ignoring a failure to reach it: the bot then refunds
    /// the viewer once its acknowledgement timeout passes, which is the right outcome when we
    /// can't say the actions ran.
    /// </summary>
    private static async Task TrySendAckAsync(Func<Task<bool>> sendAck)
    {
        try
        {
            await sendAck().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // See the summary: the bot's timeout covers an acknowledgement that never arrives.
        }
    }

    /// <summary>
    /// Runs every bot event — redemption, activity or advanced command — through the same steps:
    /// the host's <see cref="CompanionTrayAppOptions.OnBotEvent"/>, then its awaited
    /// <see cref="CompanionTrayAppOptions.OnBotEventAsync"/>, then (when the host set
    /// <see cref="CompanionTrayAppOptions.CreateEventActionDispatcher"/>) the event's mapped
    /// actions, with a balloon for any that fail. Returns the acknowledgement an advanced command
    /// or a confirmation-required redemption should send; the other events ignore it.
    /// </summary>
    private async Task<(bool Ok, string? Error)> HandleBotEventAsync(BotEventArgs botEvent)
    {
        try
        {
            _options.OnBotEvent?.Invoke(botEvent);
        }
        catch (Exception)
        {
            // Host-supplied callback — don't let it take down the shared tray process.
        }

        EventDispatchResult? hostResult = null;
        string? hostError = null;
        if (_options.OnBotEventAsync is { } handler)
        {
            try
            {
                hostResult = await handler(botEvent).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                hostError = ex.Message;
            }
        }

        if (_options.CreateEventActionDispatcher is not { } createDispatcher)
        {
            return hostError is null ? AdvancedCommandAck.FromDispatchResult(hostResult) : (false, hostError);
        }

        (bool Ok, string? Error) actionsAck;
        bool actionsMapped = false;
        try
        {
            // Off the UI thread: building the dispatcher typically loads the saved config, and
            // the actions themselves do network/BLE I/O (and Delay actions wait).
            EventDispatchResult? result = await Task.Run(() => createDispatcher().DispatchAsync(botEvent)).ConfigureAwait(true);
            actionsMapped = result is { ActionResults.Count: > 0 };
            actionsAck = AdvancedCommandAck.FromDispatchResult(result);
            ReportFailedActions(result);
        }
        catch (Exception ex)
        {
            _showBalloon("Action dispatch crashed", ex.Message);
            actionsMapped = true; // A crash is a failure, never "nothing mapped".
            actionsAck = (false, ex.Message);
        }

        // With both set, the host's own handler counts too: a host that throws, or reports a
        // failed result, must not be acknowledged as a success just because the mapped actions ran.
        if (_options.OnBotEventAsync is null)
        {
            return actionsAck;
        }

        bool hostRanActions = hostResult is { ActionResults.Count: > 0 };
        (bool Ok, string? Error) hostAck = hostError is not null ? (false, hostError)
            : hostRanActions ? AdvancedCommandAck.FromDispatchResult(hostResult)
            : (true, null);

        // Nothing mapped isn't a failure when the host's own handler ran actions for the event.
        if (!actionsMapped && hostRanActions && hostError is null)
        {
            return hostAck;
        }

        return AdvancedCommandAck.Combine(hostAck, actionsAck);
    }

    /// <summary>
    /// Surfaces actions that ran but failed (a wrong device IP, an offline device, ...) as a
    /// balloon, since Debug output is compiled out of a Release build.
    /// </summary>
    private void ReportFailedActions(EventDispatchResult? result)
    {
        if (result?.FailureSummary is { } detail)
        {
            _showBalloon("Action failed", detail);
        }
    }

    /// <summary>
    /// The <c>command.triggered</c> event handed to the host. Each command parameter is added as
    /// <c>param.&lt;key&gt;</c>, so actions can read them from the event's metadata.
    /// </summary>
    private static BotEventArgs BuildCommandEventArgs(AdvancedCommandEvent command)
    {
        var metadata = new Dictionary<string, string?>
        {
            ["eventId"] = command.Id,
            ["commandId"] = command.CommandId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["commandName"] = command.CommandName,
            ["source"] = command.Source,
            ["userLogin"] = command.UserLogin,
            ["userName"] = command.UserName,
            ["args"] = command.Args,
            ["pointsSpent"] = command.PointsSpent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["triggeredAt"] = command.TriggeredAt.ToString("o"),
        };
        foreach (var (key, value) in command.Parameters ?? [])
        {
            metadata[$"param.{key}"] = value;
        }

        return new BotEventArgs(EventActionDispatcher.CommandEventName, metadata);
    }
}
