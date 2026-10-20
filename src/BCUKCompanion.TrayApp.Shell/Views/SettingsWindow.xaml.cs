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

    /// <summary>Saves the settings and the run-at-startup choice, showing any failure in the status line.</summary>
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

    /// <summary>
    /// Logs out, revoking the token on the bot where it can be reached, and returns to the Login
    /// window. If the bot didn't confirm the token was revoked, Settings stays open to say so.
    /// </summary>
    private async void LogOutButton_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        StatusText.Text = "Logging out...";

        // Logs out locally first, then revokes the token on the bot (best-effort, a few seconds at most).
        LogoutResult result;
        try
        {
            result = await _companionClient.LogoutAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The token file couldn't be read or deleted (locked, or no access): still logged in.
            IsEnabled = true;
            StatusText.Text = $"Couldn't log out: {ex.Message}";
            return;
        }

        LoggedOut?.Invoke(this, EventArgs.Empty);
        if (result != LogoutResult.NotConfirmed)
        {
            Close();
            return;
        }

        // Logged out on this PC either way, but a copy of the token may still work on the bot.
        IsEnabled = true;
        LogOutButton.IsEnabled = false;
        StatusText.Text = "Logged out on this PC, but the bot didn't confirm the token was revoked, so a copy of it "
            + "may still work. Logging in again replaces it, or revoke it from the bot's dashboard.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
