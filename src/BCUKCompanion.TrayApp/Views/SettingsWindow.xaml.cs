using System.Windows;
using BCUKCompanion.Core;
using BCUKCompanion.TrayApp.Services;

namespace BCUKCompanion.TrayApp.Views;

public partial class SettingsWindow : Window
{
    private readonly CompanionClient _companionClient;
    private readonly AppSettings _settings;

    public event EventHandler? LoggedOut;
    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsWindow(CompanionClient companionClient, AppSettings settings, bool isConnected)
    {
        _companionClient = companionClient;
        _settings = settings;
        InitializeComponent();

        BotHostBox.Text = settings.BotHost;
        StartWithWindowsCheckBox.IsChecked = AutoStartService.IsEnabled();
        ConnectionStatusText.Text = isConnected ? "Status: connected" : "Status: not connected";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        bool botHostChanged = !string.Equals(_settings.BotHost, BotHostBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        _settings.BotHost = BotHostBox.Text.Trim();
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _settings.Save();

        AutoStartService.SetEnabled(_settings.StartWithWindows);

        StatusText.Text = botHostChanged
            ? "Saved. Restart the app for the new server URL to take effect."
            : "Saved.";

        SettingsSaved?.Invoke(this, _settings);
    }

    private void LogOutButton_Click(object sender, RoutedEventArgs e)
    {
        _companionClient.Logout();
        LoggedOut?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
