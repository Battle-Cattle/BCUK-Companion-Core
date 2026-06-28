using System.Windows;
using BCUKCompanion.Core;
using BCUKCompanion.TrayApp.Services;

namespace BCUKCompanion.TrayApp.Views;

public partial class SettingsWindow : Window
{
    private readonly CompanionClient _companionClient;
    private readonly AppSettings _settings;

    public event EventHandler? LoggedOut;

    public SettingsWindow(CompanionClient companionClient, AppSettings settings, bool isConnected)
    {
        _companionClient = companionClient;
        _settings = settings;
        InitializeComponent();

        BotHostText.Text = settings.BotHost;
        StartWithWindowsCheckBox.IsChecked = AutoStartService.IsEnabled();
        ConnectionStatusText.Text = isConnected ? "Status: connected" : "Status: not connected";
    }

    /// <summary>Reflects a bot host changed elsewhere (the Login window's own server field) while this window is open.</summary>
    internal void RefreshBotHost(string botHost)
    {
        BotHostText.Text = botHost;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _settings.Save();

        AutoStartService.SetEnabled(_settings.StartWithWindows);

        StatusText.Text = "Saved.";
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
