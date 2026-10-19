using System.IO;
using System.Windows;
using BCUKCompanion.Core;
using BCUKCompanion.TrayApp.Services;

namespace BCUKCompanion.TrayApp.Views;

public partial class SettingsWindow : Window
{
    private CompanionClient _companionClient;
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

    /// <summary>Re-points this still-open window at a companion client created for a newly saved bot host.</summary>
    internal void UpdateCompanionClient(CompanionClient companionClient)
    {
        _companionClient = companionClient;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            _settings.Save();
            AutoStartService.SetEnabled(_settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            // A locked/read-only settings.json or a registry error: say so here rather than crash.
            StatusText.Text = $"Couldn't save: {ex.Message}";
            return;
        }

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
