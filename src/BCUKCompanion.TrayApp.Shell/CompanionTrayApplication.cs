using System.Windows;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.TrayApp.Services;
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
        var app = new CompanionTrayApplication(options ?? new CompanionTrayAppOptions());
        ((System.Windows.Application)app).Run();
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
            System.Windows.MessageBox.Show("BCUK Companion is already running — check your system tray.", "BCUK Companion");
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _botHost = new Uri(_settings.BotHost);
        _companionClient = CreateCompanionClient(_botHost);

        _trayIcon = new TrayIconController();
        _trayIcon.OpenLoginRequested += (_, _) => ShowLoginWindow();
        _trayIcon.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        _trayIcon.ExitRequested += (_, _) => Shutdown();

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

        _loginWindow = new LoginWindow(_companionClient, _settings.BotHost);
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

        _settingsWindow = new SettingsWindow(_companionClient, _settings, _isConnected);
        _settingsWindow.LoggedOut += (_, _) => ShowLoginWindow();
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        ApplyBotHostChange(settings.BotHost);
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
    /// user subsequently clicks Login.
    /// </summary>
    private void ApplyBotHostChange(string newBotHost)
    {
        if (string.Equals(_botHost.OriginalString, newBotHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool wasLoggedIn = _companionClient.IsLoggedIn;

        _companionClient.Events.RedemptionReceived -= OnRedemptionReceived;
        _companionClient.Events.ConnectionStateChanged -= OnConnectionStateChanged;
        _companionClient.Dispose();

        _botHost = new Uri(newBotHost);
        _companionClient = CreateCompanionClient(_botHost);
        _loginWindow?.UpdateCompanionClient(_companionClient, newBotHost);
        _settingsWindow?.RefreshBotHost(newBotHost);

        if (wasLoggedIn)
        {
            _companionClient.StartListening();
        }
    }

    private CompanionClient CreateCompanionClient(Uri botHost)
    {
        var client = new CompanionClient(botHost, new DpapiFileTokenStore(AppPaths.TokenFile));
        client.Events.RedemptionReceived += OnRedemptionReceived;
        client.Events.ConnectionStateChanged += OnConnectionStateChanged;
        return client;
    }

    private void OnRedemptionReceived(object? sender, RedemptionEvent redemption)
    {
        Dispatcher.Invoke(() =>
        {
            string who = string.IsNullOrEmpty(redemption.UserName) ? redemption.UserLogin : redemption.UserName;
            _trayIcon.ShowBalloon(redemption.RewardTitle, $"Redeemed by {who}");
        });
    }

    private void OnConnectionStateChanged(object? sender, CompanionConnectionState state)
    {
        Dispatcher.Invoke(() =>
        {
            _isConnected = state == CompanionConnectionState.Connected;
            _trayIcon.SetStatus($"BCUK Companion — {state}");

            if (state == CompanionConnectionState.AuthenticationFailed)
            {
                _companionClient.StopListening();
                ShowLoginWindow();
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
