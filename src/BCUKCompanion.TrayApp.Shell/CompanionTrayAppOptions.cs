using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;
using BCUKCompanion.TrayApp.Updates;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// Per-host customization for <see cref="CompanionTrayApplication"/>. Hosts that install
/// alongside other companion apps on the same Windows account should set
/// <see cref="DataFolderName"/> to a unique value to avoid colliding on settings/token
/// storage, the single-instance lock, and the Windows startup registry entry.
/// </summary>
public sealed class CompanionTrayAppOptions
{
    public string DataFolderName { get; init; } = "BCUKCompanion";

    /// <summary>
    /// The app's name as shown to the user: the tray icon's tooltip, window titles, the
    /// "already running" message and the shell's own balloons. Set it to tell apart several
    /// companion apps running on the same Windows account.
    /// </summary>
    public string DisplayName { get; init; } = "BCUK Companion";

    /// <summary>
    /// Turns on self-updating from the app's GitHub Releases: a silent check-and-install at launch
    /// (for release builds only) and a "Check for updates..." tray menu item. Null (the default)
    /// leaves updating off.
    /// </summary>
    public CompanionUpdateOptions? Updates { get; init; }

    /// <summary>
    /// Builds the dispatcher that runs a bot event's mapped actions. When set, the shell itself
    /// handles every redemption and advanced command the same way: it runs the matched actions,
    /// shows a balloon for any that fail, and acknowledges advanced commands from the result. The
    /// companion app only contributes its own action kinds (see <see cref="EventActionTypeRegistry"/>)
    /// and whatever context they need. Called once per event, on a background thread, so it can
    /// load the latest saved mappings and devices each time.
    /// </summary>
    public Func<EventActionDispatcher>? CreateEventActionDispatcher { get; init; }

    /// <summary>
    /// Invoked on the UI thread for every bot event (redemption, activity or advanced command),
    /// so a companion app can react to it without owning any bot-connection logic itself.
    /// </summary>
    public Action<BotEventArgs>? OnBotEvent { get; init; }

    /// <summary>
    /// Invoked on the UI thread for every bot event (redemption, activity or advanced command),
    /// after <see cref="OnBotEvent"/>, and awaited. Most apps should set
    /// <see cref="CreateEventActionDispatcher"/> instead and leave this unset. Without a
    /// dispatcher, the shell acknowledges an advanced command (<c>command.triggered</c>) from
    /// the result returned here: anything but every mapped action succeeding refunds the viewer,
    /// and so does leaving both unset, since then nothing ran the command. With a dispatcher, the
    /// acknowledgement comes from the dispatcher's result instead.
    /// </summary>
    public Func<BotEventArgs, Task<EventDispatchResult?>>? OnBotEventAsync { get; init; }

    /// <summary>
    /// Extra entries shown in the tray icon's context menu, letting a companion app surface
    /// its own settings UI (or any other action) from the shared tray icon.
    /// </summary>
    public IReadOnlyList<TrayMenuItem>? AdditionalMenuItems { get; init; }

    /// <summary>
    /// Invoked on the UI thread whenever the shell's <see cref="CompanionClient"/> instance is
    /// created or replaced (e.g. after a bot-host change in Settings), so a companion app can
    /// make its own on-demand calls against it — such as <see cref="CompanionClient.GetRewardsAsync"/>
    /// to populate a reward picker — without owning any login/connection lifecycle itself.
    /// </summary>
    public Action<CompanionClient>? OnClientReady { get; init; }

    /// <summary>
    /// Invoked once at startup with a callback the host can call as
    /// <c>showBalloon(title, text)</c> to display a tray balloon notification of its own,
    /// alongside the shell's built-in redemption/activity balloons. The callback is safe to
    /// call from any thread — it marshals onto the UI thread internally.
    /// </summary>
    public Action<Action<string, string>>? OnTrayIconReady { get; init; }
}
