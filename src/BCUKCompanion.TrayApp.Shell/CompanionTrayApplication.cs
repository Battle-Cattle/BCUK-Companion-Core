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
        _companionClient = new CompanionClient(new Uri(_settings.BotHost), new DpapiFileTokenStore(AppPaths.TokenFile));
        _companionClient.Events.RedemptionReceived += OnRedemptionReceived;
        _companionClient.Events.ConnectionStateChanged += OnConnectionStateChanged;

        _trayIcon = new TrayIconController(_options.AdditionalMenuItems);
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
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnRedemptionReceived(object? sender, RedemptionEvent redemption)
    {
        Dispatcher.Invoke(() =>
        {
            string who = string.IsNullOrEmpty(redemption.UserName) ? redemption.UserLogin : redemption.UserName;
            _trayIcon.ShowBalloon(redemption.RewardTitle, $"Redeemed by {who}");

            try
            {
                _options.OnBotEvent?.Invoke(new BotEventArgs(
                    redemption.RewardTitle,
                    new Dictionary<string, string?>
                    {
                        ["rewardId"] = redemption.RewardId,
                        ["userLogin"] = redemption.UserLogin,
                        ["userName"] = redemption.UserName,
                        ["userInput"] = redemption.UserInput,
                        ["redeemedAt"] = redemption.RedeemedAt.ToString("o"),
                    }));
            }
            catch (Exception)
            {
                // Host-supplied callback — don't let it take down the shared tray process.
            }
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
