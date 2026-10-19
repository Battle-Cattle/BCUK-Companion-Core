using System.IO;
using System.Windows;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.Core.Updates;
using BCUKCompanion.TrayApp.Services;
using BCUKCompanion.TrayApp.Updates;
using BCUKCompanion.TrayApp.Views;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// The full tray-app shell (single-instance guard, login/settings windows, tray icon,
/// balloon notifications). Hosts call <see cref="Run"/> from a minimal Program.cs.
/// </summary>
public sealed class CompanionTrayApplication : System.Windows.Application
{
    private readonly CompanionTrayAppOptions _options;
    private Mutex? _singleInstanceMutex;
    private AppSettings _settings = null!;
    private Uri _botHost = null!;
    private CompanionClient _companionClient = null!;
    private TrayIconController _trayIcon = null!;
    private LoginWindow? _loginWindow;
    private SettingsWindow? _settingsWindow;
    private bool _isConnected;

    private CompanionTrayApplication(CompanionTrayAppOptions options)
    {
        _options = options;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public static void Run(CompanionTrayAppOptions? options = null)
    {
        RemoveLegacyInstallFiles();
        var app = new CompanionTrayApplication(options ?? new CompanionTrayAppOptions());
        ((System.Windows.Application)app).Run();
    }

    // Releases are single-file builds, and the updater never deletes anything, so an install
    // updated from an older multi-file release still holds that build's loose runtime files. Runs
    // before WPF starts so none of them are loaded yet. Only a single-file build (no assembly
    // Location) may do this: a multi-file build, e.g. a developer's, still needs those files.
    private static void RemoveLegacyInstallFiles()
    {
        if (!string.IsNullOrEmpty(typeof(CompanionTrayApplication).Assembly.Location) || Environment.ProcessPath is not { } exePath)
        {
            return;
        }

        try
        {
            LegacyInstallCleanup.Run(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(exePath));
        }
        catch (Exception)
        {
            // Best-effort clean-up; it must never stop the app starting. Retried on the next launch.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Configure(_options.DataFolderName);
        AutoStartService.Configure(_options.DataFolderName);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, $"{_options.DataFolderName}.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the mutex — don't try to release a lock we never acquired.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            System.Windows.MessageBox.Show($"{_options.DisplayName} is already running — check your system tray.", _options.DisplayName);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _botHost = new Uri(_settings.BotHost);
        _companionClient = CreateCompanionClient(_botHost);

        // The updater's balloons go through _trayIcon, which exists by the time it shows any.
        Action<string, string> showBalloon = (title, text) => Dispatcher.Invoke(() => _trayIcon.ShowBalloon(title, text));
        CompanionUpdater? updater = _options.Updates is { } updateOptions
            ? new CompanionUpdater(updateOptions, _options.DisplayName, _options.DataFolderName, showBalloon)
            : null;

        var menuItems = new List<TrayMenuItem>(_options.AdditionalMenuItems ?? []);
        if (updater is not null)
        {
            menuItems.Add(updater.CreateMenuItem());
        }

        _trayIcon = new TrayIconController(_options.DisplayName, menuItems);
        try
        {
            // Host-supplied callback: isolate it (same as OnClientReady/OnBotEvent) so a
            // faulty host doesn't take down startup, and marshal ShowBalloon onto the UI
            // thread since hosts commonly call it from background dispatch (e.g. OnBotEvent).
            _options.OnTrayIconReady?.Invoke(showBalloon);
        }
        catch (Exception)
        {
            // Host-supplied callback — do not let it take down the shared tray process.
        }
        _trayIcon.OpenLoginRequested += (_, _) => ShowLoginWindow();
        _trayIcon.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        // Shut down once the dispatcher is idle, so queued work (e.g. a SaveLastActivityEventId
        // posted by the event stream) runs first rather than being aborted with the dispatcher.
        _trayIcon.ExitRequested += (_, _) =>
            Dispatcher.InvokeAsync(Shutdown, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        if (_options.Updates?.CheckOnLaunch == true)
        {
            updater?.CheckOnLaunch();
        }

        if (_companionClient.IsLoggedIn)
        {
            _companionClient.StartListening();
        }
        else
        {
            ShowLoginWindow();
        }
    }

    private void ShowLoginWindow()
    {
        if (_loginWindow is not null)
        {
            _loginWindow.Activate();
            return;
        }

        _loginWindow = new LoginWindow(_companionClient, _settings.BotHost) { Title = $"{_options.DisplayName} — Login" };
        _loginWindow.LoginSucceeded += (_, _) => _companionClient.StartListening();
        _loginWindow.ServerHostChanged += OnLoginServerHostChanged;
        _loginWindow.Closed += (_, _) => _loginWindow = null;
        _loginWindow.Show();
        _loginWindow.Activate();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_companionClient, _settings, _isConnected) { Title = $"{_options.DisplayName} — Settings" };
        _settingsWindow.LoggedOut += (_, _) => ShowLoginWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// The Login window's own server field doesn't go through Settings, since it
    /// needs to work before the user is logged in — persist it the same way Settings does.
    /// </summary>
    private void OnLoginServerHostChanged(object? sender, string newBotHost)
    {
        _settings.BotHost = newBotHost;
        _settings.Save();
        ApplyBotHostChange(newBotHost);
    }

    /// <summary>
    /// Re-points the companion client at the new bot host without making any
    /// network call — login (and any resulting connection) only happens if the
    /// user subsequently clicks Login. The saved token was issued by the old bot, so a
    /// different server (scheme, host or port) discards it rather than ever sending it to the
    /// new one; a change that keeps the same server (e.g. only a path) stays logged in.
    /// </summary>
    private void ApplyBotHostChange(string newBotHost)
    {
        if (string.Equals(_botHost.OriginalString, newBotHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var newHost = new Uri(newBotHost);
        bool sameServer = AppSettings.ActivityKey(newHost) == AppSettings.ActivityKey(_botHost);
        bool keepListening = sameServer && _companionClient.IsLoggedIn;

        _companionClient.Events.RedemptionReceived -= OnRedemptionReceived;
        _companionClient.Events.ActivityReceived -= OnActivityReceived;
        _companionClient.Events.AdvancedCommandReceived -= OnAdvancedCommandReceived;
        _companionClient.Events.ConnectionStateChanged -= OnConnectionStateChanged;
        _companionClient.ListenLoopFaulted -= OnListenLoopFaulted;
        if (!sameServer)
        {
            _companionClient.Logout();
            _isConnected = false;
            _trayIcon.SetStatus(_options.DisplayName, CompanionConnectionState.Disconnected.ToString());
        }
        _companionClient.Dispose();

        _botHost = newHost;
        _companionClient = CreateCompanionClient(_botHost);
        _loginWindow?.UpdateCompanionClient(_companionClient, newBotHost);
        _settingsWindow?.UpdateCompanionClient(_companionClient);
        _settingsWindow?.RefreshBotHost(newBotHost);

        if (keepListening)
        {
            _companionClient.StartListening();
        }
    }

    /// <summary>
    /// Creates the client for <paramref name="botHost"/>, wires its events to the tray, and
    /// seeds its activity stream with the last activity id shown for that host.
    /// </summary>
    private CompanionClient CreateCompanionClient(Uri botHost)
    {
        var client = new CompanionClient(botHost, new DpapiFileTokenStore(AppPaths.TokenFile));
        client.Events.RedemptionReceived += OnRedemptionReceived;
        client.Events.ActivityReceived += OnActivityReceived;
        client.Events.AdvancedCommandReceived += OnAdvancedCommandReceived;
        client.Events.ConnectionStateChanged += OnConnectionStateChanged;
        client.ListenLoopFaulted += OnListenLoopFaulted;

        // The connect-time catch-up returns recent activity whether or not a previous run already
        // showed it, so seed the stream with the last id this app showed for this bot host. With
        // nothing saved yet (first run, or a new host), only record where the server is up to.
        string activityKey = AppSettings.ActivityKey(botHost);
        if (_settings.LastActivityEventIds.TryGetValue(activityKey, out long lastActivityEventId) && lastActivityEventId > 0)
        {
            client.Events.LastActivityEventId = lastActivityEventId;
        }
        else
        {
            client.Events.BaselineOnFirstCatchUp = true;
        }
        client.Events.LastActivityEventIdChanged += (_, id) =>
            _ = Dispatcher.InvokeAsync(() => SaveLastActivityEventId(activityKey, id));

        try
        {
            _options.OnClientReady?.Invoke(client);
        }
        catch (Exception)
        {
            // Host-supplied callback — don't let it take down the shared tray process.
        }

        return client;
    }

    /// <summary>
    /// Shows a balloon for a redemption and hands it to the host's handlers and mapped actions,
    /// then acknowledges it to the bot when its reward requires companion confirmation — a
    /// failure there cancels the redemption and refunds the viewer. While those actions run, the
    /// bot gets a progress ping every minute so long actions aren't refunded mid-run.
    /// </summary>
    private void OnRedemptionReceived(object? sender, RedemptionEvent redemption)
    {
        // Captured now: a bot-host change can replace _companionClient while the actions run,
        // and the acknowledgement must go to the host the event came from.
        CompanionClient client = _companionClient;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            _trayIcon.ShowBalloon(redemption.RewardTitle, $"Redeemed by {redemption.DisplayName}");

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

    private static readonly Dictionary<string, string> ActivityTitles = new(StringComparer.Ordinal)
    {
        ["follow"] = "New Follower",
        ["sub"] = "New Sub",
        ["resub"] = "Resub",
        ["giftsub"] = "Gift Sub",
        ["raid"] = "Raid",
    };

    /// <summary>
    /// Persists the newest activity id shown for a bot host. Runs on the UI thread, like every
    /// other settings save. Best-effort: failing to save only means one repeated notification
    /// on the next launch.
    /// </summary>
    private void SaveLastActivityEventId(string activityKey, long id)
    {
        if (_settings.LastActivityEventIds.TryGetValue(activityKey, out long saved) && saved >= id)
        {
            return;
        }

        _settings.LastActivityEventIds[activityKey] = id;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Shows a balloon for a follow/sub/raid/... and hands it to the host's handlers. Queued on the
    /// UI thread ahead of the matching <see cref="SaveLastActivityEventId"/>, so the id is only
    /// saved once the activity has been handled.
    /// </summary>
    private void OnActivityReceived(object? sender, CompanionActivityEvent activity)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            string title = ActivityTitles.TryGetValue(activity.Type, out string? knownTitle) ? knownTitle : activity.Type;
            string text = string.IsNullOrEmpty(activity.Detail)
                ? activity.DisplayName
                : $"{activity.DisplayName} — {activity.Detail}";
            _trayIcon.ShowBalloon(title, text);

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
    private void OnAdvancedCommandReceived(object? sender, AdvancedCommandEvent command)
    {
        // Captured now: a bot-host change can replace _companionClient while the actions run,
        // and the acknowledgement must go to the host the event came from.
        CompanionClient client = _companionClient;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            string text = command.Source == "streamdeck"
                ? "Triggered from Stream Deck"
                : $"Used by {(string.IsNullOrEmpty(command.UserName) ? command.UserLogin : command.UserName)}";
            _trayIcon.ShowBalloon(command.CommandName, text);

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

        try
        {
            // Off the UI thread: building the dispatcher typically loads the saved config, and
            // the actions themselves do network/BLE I/O (and Delay actions wait).
            EventDispatchResult? result = await Task.Run(() => createDispatcher().DispatchAsync(botEvent)).ConfigureAwait(true);
            var ack = AdvancedCommandAck.FromDispatchResult(result);
            ReportFailedActions(result);
            return ack;
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloon("Action dispatch crashed", ex.Message);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Surfaces actions that ran but failed (a wrong device IP, an offline device, ...) as a
    /// balloon, since Debug output is compiled out of a Release build.
    /// </summary>
    private void ReportFailedActions(EventDispatchResult? result)
    {
        if (result?.FailureSummary is { } detail)
        {
            _trayIcon.ShowBalloon("Action failed", detail);
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

    private void OnConnectionStateChanged(object? sender, CompanionConnectionState state)
    {
        Dispatcher.Invoke(() =>
        {
            _isConnected = state == CompanionConnectionState.Connected;
            _trayIcon.SetStatus(_options.DisplayName, state.ToString());

            if (state == CompanionConnectionState.AuthenticationFailed)
            {
                _companionClient.StopListening();
                ShowLoginWindow();
            }
        });
    }

    /// <summary>
    /// The listen loop ended because of an unexpected exception rather than a
    /// deliberate StopListening()/restart. Left unhandled, the tray icon would
    /// silently freeze on its last status with no further events ever
    /// arriving — so surface it and restart listening if we're still logged in.
    /// </summary>
    private void OnListenLoopFaulted(object? sender, Exception ex)
    {
        Dispatcher.Invoke(() =>
        {
            _trayIcon.ShowBalloon(_options.DisplayName, "Lost connection to the event stream — reconnecting…");

            if (_companionClient.IsLoggedIn)
            {
                _companionClient.StartListening();
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _companionClient?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
