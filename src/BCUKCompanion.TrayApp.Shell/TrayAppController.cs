using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.Core.Updates;
using BCUKCompanion.TrayApp.Services;
using BCUKCompanion.TrayApp.Updates;
using BCUKCompanion.TrayApp.Views;

namespace BCUKCompanion.TrayApp;

/// <summary>What the tray app's controller needs from the WPF application hosting it.</summary>
/// <param name="Dispatcher">The UI thread's dispatcher; windows, balloons and settings saves run on it.</param>
/// <param name="Shutdown">Ends the application.</param>
/// <param name="ShowMessage">Shows a message box, given its text and caption.</param>
/// <param name="HttpHandler">Sends the companion client's requests; null for the real network.</param>
internal sealed record TrayAppEnvironment(
    Dispatcher Dispatcher,
    Action Shutdown,
    Action<string, string> ShowMessage,
    HttpMessageHandler? HttpHandler = null);

/// <summary>
/// Everything the tray app does once WPF has started: the single-instance guard, settings,
/// companion client, tray icon, updater, and the login and settings windows. Kept apart from
/// <see cref="CompanionTrayApplication"/> (only one WPF application can exist per process) so
/// tests can run it on a dispatcher of their own.
/// </summary>
internal sealed class TrayAppController : IDisposable
{
    private readonly CompanionTrayAppOptions _options;
    private readonly TrayAppEnvironment _environment;
    private Mutex? _singleInstanceMutex;
    // Start() assigns these before anything that reads them can run: the tray menu, the windows
    // and the client's event handlers all start from Start(). A Start() that loses the single-instance
    // lock leaves them null, which is why ReportUnhandledError checks _trayIcon for null and
    // Dispose uses ?. on _companionClient and _trayIcon.
    private AppSettings _settings = null!; // set by Start()
    private Uri _botHost = null!; // set by Start()
    private CompanionClient _companionClient = null!; // set by Start(); null-checked in Dispose
    private TrayIconController _trayIcon = null!; // set by Start(); null-checked in ReportUnhandledError and Dispose
    private BotEventHandler _botEvents = null!; // set by Start()
    private LoginWindow? _loginWindow;
    private SettingsWindow? _settingsWindow;
    private bool _isConnected;

    /// <summary>Creates the controller; nothing starts until <see cref="Start"/>.</summary>
    /// <param name="options">The host app's identity, integrations and update settings.</param>
    /// <param name="environment">The dispatcher, shutdown and message box of the application hosting it.</param>
    public TrayAppController(CompanionTrayAppOptions options, TrayAppEnvironment environment)
    {
        _options = options;
        _environment = environment;
    }

    /// <summary>The tray icon; read it only after <see cref="Start"/> has returned true.</summary>
    internal TrayIconController TrayIcon => _trayIcon;

    /// <summary>The current companion client; read it only after <see cref="Start"/> has returned true.</summary>
    internal CompanionClient Client => _companionClient;

    /// <summary>The open login window, if any.</summary>
    internal LoginWindow? LoginWindow => _loginWindow;

    /// <summary>The open settings window, if any.</summary>
    internal SettingsWindow? SettingsWindow => _settingsWindow;

    /// <summary>
    /// Deletes the loose files an older multi-file release left in the install folder. Only a
    /// single-file build (no assembly location) may do this: a multi-file build, e.g. a
    /// developer's, still needs those files. Best-effort; a failure is retried on the next launch.
    /// </summary>
    /// <param name="assemblyLocation">The Shell assembly's location; empty in a single-file build.</param>
    /// <param name="processPath">The running exe's path, or null if unknown.</param>
    /// <param name="baseDirectory">The install folder.</param>
    /// <returns>True if the clean-up ran to completion; false if skipped or it failed.</returns>
    internal static bool RemoveLegacyInstallFiles(string assemblyLocation, string? processPath, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(assemblyLocation) || processPath is null)
        {
            return false;
        }

        try
        {
            LegacyInstallCleanup.Run(baseDirectory, Path.GetFileNameWithoutExtension(processPath));
            return true;
        }
        catch (Exception)
        {
            // Best-effort clean-up; it must never stop the app starting. Retried on the next launch.
            return false;
        }
    }

    /// <summary>
    /// Takes the single-instance lock, loads the settings, creates the client, tray icon and
    /// updater, then starts listening or shows the Login window. If another instance already
    /// holds the lock, says so and shuts the application down instead.
    /// </summary>
    /// <returns>True once started; false if another instance is already running.</returns>
    public bool Start()
    {
        AppPaths.Configure(_options.DataFolderName);
        AutoStartService.Configure(_options.DataFolderName);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, $"{_options.DataFolderName}.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the mutex — don't try to release a lock we never acquired.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _environment.ShowMessage($"{_options.DisplayName} is already running — check your system tray.", _options.DisplayName);
            _environment.Shutdown();
            return false;
        }

        _settings = AppSettings.Load();
        // Its balloons go through _trayIcon, which exists before any bot event can arrive.
        _botEvents = new BotEventHandler(
            _options, DispatcherScheduler.For(_environment.Dispatcher), (title, text) => _trayIcon.ShowBalloon(title, text));
        _botHost = new Uri(_settings.BotHost);
        _companionClient = CreateCompanionClient(_botHost);

        // The updater's balloons go through _trayIcon, which exists by the time it shows any.
        Action<string, string> showBalloon = (title, text) => _environment.Dispatcher.Invoke(() => _trayIcon.ShowBalloon(title, text));
        CompanionUpdater? updater = _options.Updates is { } updateOptions
            ? new CompanionUpdater(updateOptions, _options.DisplayName, _options.DataFolderName, showBalloon)
            : null;

        _trayIcon = CreateTrayIcon(updater);
        InvokeOnTrayIconReady(showBalloon);

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

        return true;
    }

    /// <summary>Creates the tray icon with the host's menu items (and the updater's), wired to the windows and Exit.</summary>
    /// <param name="updater">Adds a "Check for updates..." item when the host enabled updates; otherwise null.</param>
    /// <returns>The visible tray icon.</returns>
    private TrayIconController CreateTrayIcon(CompanionUpdater? updater)
    {
        var menuItems = new List<TrayMenuItem>(_options.AdditionalMenuItems ?? []);
        if (updater is not null)
        {
            menuItems.Add(updater.CreateMenuItem());
        }

        var trayIcon = new TrayIconController(_options.DisplayName, menuItems);
        trayIcon.MenuItemFailed += (_, failure) =>
            trayIcon.ShowBalloon($"{failure.Label} failed", failure.Error.Message);
        trayIcon.OpenLoginRequested += (_, _) => ShowLoginWindow();
        trayIcon.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        // Shut down once the dispatcher is idle, so queued work (e.g. a SaveLastActivityEventId
        // posted by the event stream) runs first rather than being aborted with the dispatcher.
        trayIcon.ExitRequested += (_, _) =>
            _environment.Dispatcher.InvokeAsync(_environment.Shutdown, DispatcherPriority.ApplicationIdle);
        return trayIcon;
    }

    /// <summary>Hands the host its balloon callback, ignoring anything the host's callback throws.</summary>
    /// <param name="showBalloon">Shows a balloon from any thread.</param>
    private void InvokeOnTrayIconReady(Action<string, string> showBalloon)
    {
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
    }

    /// <summary>Shows an otherwise-unhandled error as a balloon, once the tray icon exists.</summary>
    /// <param name="ex">The exception whose message the balloon shows.</param>
    /// <returns>
    /// True if it was reported; false before the tray icon exists (a startup failure), when there's
    /// nowhere to report it and swallowing it would leave an invisible process holding the lock.
    /// </returns>
    public bool ReportUnhandledError(Exception ex)
    {
        if (_trayIcon is null)
        {
            return false;
        }

        _trayIcon.ShowBalloon($"{_options.DisplayName} hit an error", ex.Message);
        return true;
    }

    /// <summary>Shows the login window, or brings the open one to the front.</summary>
    internal void ShowLoginWindow()
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

    /// <summary>Shows the settings window, or brings the open one to the front.</summary>
    internal void ShowSettingsWindow()
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
    /// Applied before it's saved: if the old token can't be discarded, the change throws with
    /// nothing saved, so the next launch can't send that token to the new server.
    /// </summary>
    /// <param name="sender">The Login window whose server field changed (unused).</param>
    /// <param name="newBotHost">The new bot URL the user entered.</param>
    private void OnLoginServerHostChanged(object? sender, string newBotHost)
    {
        ApplyBotHostChange(newBotHost);
        _settings.BotHost = newBotHost;
        _settings.Save();
    }

    /// <summary>
    /// Re-points the companion client at the new bot host without contacting the new one —
    /// login (and any resulting connection) only happens if the user subsequently clicks Login.
    /// The saved token was issued by the old bot, so a different server (scheme, host or port)
    /// discards it rather than ever sending it to the new one, and asks the old bot to revoke it
    /// in the background so a copy of it stops working there too; a change that keeps the same
    /// server (e.g. only a path) stays logged in.
    /// </summary>
    /// <param name="newBotHost">The new bot URL; nothing changes if it equals the current one, ignoring case.</param>
    private void ApplyBotHostChange(string newBotHost)
    {
        if (string.Equals(_botHost.OriginalString, newBotHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var oldClient = _companionClient;
        var newHost = new Uri(newBotHost);
        bool sameServer = AppSettings.ActivityKey(newHost) == AppSettings.ActivityKey(_botHost);
        bool keepListening = sameServer && oldClient.IsLoggedIn;

        // First, so a token file that can't be deleted throws before anything is detached and
        // leaves the app exactly as it was (still wired to the old client). LogoutAsync discards
        // the token before it returns; only the revoke on the old bot is left running.
        Task<LogoutResult>? oldServerRevoke = sameServer ? null : oldClient.LogoutAsync();

        DetachClientEvents(oldClient);
        if (!sameServer)
        {
            _isConnected = false;
            _trayIcon.SetStatus(_options.DisplayName, CompanionConnectionState.Disconnected.ToString());
        }

        if (oldServerRevoke is null)
        {
            oldClient.Dispose();
        }
        else
        {
            _ = FinishOldServerLogoutAsync(oldClient, oldServerRevoke);
        }

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

    /// <summary>Stops a replaced client's events reaching the tray.</summary>
    /// <param name="client">The client being replaced.</param>
    private void DetachClientEvents(CompanionClient client)
    {
        client.Events.RedemptionReceived -= OnRedemptionReceived;
        client.Events.ActivityReceived -= OnActivityReceived;
        client.Events.AdvancedCommandReceived -= OnAdvancedCommandReceived;
        client.Events.ConnectionStateChanged -= OnConnectionStateChanged;
        client.ListenLoopFaulted -= OnListenLoopFaulted;
    }

    /// <summary>
    /// Waits for the old bot's revoke after a server change (a few seconds at most), says so if it
    /// wasn't confirmed, then disposes the old client, which owns the connection the revoke uses.
    /// </summary>
    /// <param name="oldClient">The client for the old bot host, disposed once the revoke ends.</param>
    /// <param name="revoke">The old client's logout, whose result says whether the old bot confirmed the revoke.</param>
    /// <returns>A task that completes once the old client is disposed; it faults only if the revoke itself threw.</returns>
    private async Task FinishOldServerLogoutAsync(CompanionClient oldClient, Task<LogoutResult> revoke)
    {
        try
        {
            if (await revoke.ConfigureAwait(true) == LogoutResult.NotConfirmed)
            {
                _trayIcon.ShowBalloon(_options.DisplayName,
                    "Switched server, but the old server didn't confirm the old login was revoked, so a copy of it may still work there.");
            }
        }
        finally
        {
            oldClient.Dispose();
        }
    }

    /// <summary>
    /// Creates the client for <paramref name="botHost"/>, wires its events to the tray, and
    /// seeds its activity stream with the last activity id shown for that host.
    /// </summary>
    /// <param name="botHost">The bot's base URL.</param>
    /// <returns>The new client, not yet listening, after the host's <c>OnClientReady</c> callback has seen it.</returns>
    private CompanionClient CreateCompanionClient(Uri botHost)
    {
        var httpClient = _environment.HttpHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : null;
        var client = new CompanionClient(botHost, new DpapiFileTokenStore(AppPaths.TokenFile), httpClient);
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
            _ = _environment.Dispatcher.InvokeAsync(() => SaveLastActivityEventId(activityKey, id));

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

    // The client is read when the event arrives: a bot-host change can replace _companionClient
    // while the actions run, and the acknowledgement must go to the host the event came from.
    /// <summary>Hands a redemption to the bot-event handler, with the client it arrived on.</summary>
    /// <param name="sender">The client's event stream (unused).</param>
    /// <param name="redemption">The channel-point redemption that arrived.</param>
    internal void OnRedemptionReceived(object? sender, RedemptionEvent redemption) =>
        _botEvents.OnRedemption(_companionClient, redemption);

    /// <summary>Hands a follow/sub/raid/... activity to the bot-event handler.</summary>
    /// <param name="sender">The client's event stream (unused).</param>
    /// <param name="activity">The activity event that arrived.</param>
    internal void OnActivityReceived(object? sender, CompanionActivityEvent activity) =>
        _botEvents.OnActivity(activity);

    /// <summary>Hands an advanced command to the bot-event handler, with the client it arrived on.</summary>
    /// <param name="sender">The client's event stream (unused).</param>
    /// <param name="command">The advanced command event that arrived.</param>
    internal void OnAdvancedCommandReceived(object? sender, AdvancedCommandEvent command) =>
        _botEvents.OnAdvancedCommand(_companionClient, command);

    /// <summary>
    /// Persists the newest activity id shown for a bot host. Runs on the UI thread, like every
    /// other settings save. Best-effort: failing to save only means one repeated notification
    /// on the next launch.
    /// </summary>
    /// <param name="activityKey">The bot host's key, from <see cref="AppSettings.ActivityKey"/>.</param>
    /// <param name="id">The newest activity event id shown; ignored unless it's above the saved one.</param>
    internal void SaveLastActivityEventId(string activityKey, long id)
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

    /// <summary>Shows the connection state on the tray icon; a rejected token is discarded and the Login window opened.</summary>
    /// <param name="sender">The client's event stream (unused).</param>
    /// <param name="state">The stream's new connection state.</param>
    internal void OnConnectionStateChanged(object? sender, CompanionConnectionState state)
    {
        _environment.Dispatcher.Invoke(() =>
        {
            _isConnected = state == CompanionConnectionState.Connected;
            _trayIcon.SetStatus(_options.DisplayName, state.ToString());

            if (state == CompanionConnectionState.AuthenticationFailed)
            {
                OnAuthenticationFailed();
            }
        });
    }

    /// <summary>
    /// The bot rejected the token (revoked or replaced): discards it, so the next launch doesn't
    /// retry it and the Login window starts from a logged-out state, then opens the Login window.
    /// Only that token, though: one saved since (a new login while this loop was connecting) is
    /// kept, and then there's nothing to do. Runs on the listen loop's thread via Invoke, so a
    /// token file that can't be deleted is reported rather than thrown back into (and faulting) the loop.
    /// </summary>
    private void OnAuthenticationFailed()
    {
        try
        {
            if (!_companionClient.DiscardRejectedToken())
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Traced too, since the balloon is best-effort and may not show.
            System.Diagnostics.Trace.TraceError("Couldn't discard the rejected login: {0}", ex);
            _companionClient.StopListening();
            _trayIcon.ShowBalloon(_options.DisplayName, $"Couldn't discard the rejected login: {ex.Message}");
        }

        ShowLoginWindow();
    }

    /// <summary>
    /// The listen loop ended because of an unexpected exception rather than a
    /// deliberate StopListening()/restart. Left unhandled, the tray icon would
    /// silently freeze on its last status with no further events ever
    /// arriving — so surface it and restart listening if we're still logged in.
    /// </summary>
    /// <param name="sender">The <see cref="CompanionClient"/> whose loop faulted (unused).</param>
    /// <param name="ex">The exception that ended the loop (unused; the balloon shows a fixed message).</param>
    internal void OnListenLoopFaulted(object? sender, Exception ex)
    {
        _environment.Dispatcher.Invoke(() =>
        {
            _trayIcon.ShowBalloon(_options.DisplayName, "Lost connection to the event stream — reconnecting…");

            if (_companionClient.IsLoggedIn)
            {
                _companionClient.StartListening();
            }
        });
    }

    /// <summary>Disconnects, removes the tray icon and releases the single-instance lock.</summary>
    public void Dispose()
    {
        _companionClient?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }
}
