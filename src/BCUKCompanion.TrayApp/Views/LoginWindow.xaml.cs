using System.Diagnostics;
using System.Windows;
using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Views;

public partial class LoginWindow : Window
{
    private readonly CompanionClient _companionClient;

    public event EventHandler? LoginSucceeded;

    public LoginWindow(CompanionClient companionClient, string botHost)
    {
        _companionClient = companionClient;
        InitializeComponent();
        BotHostText.Text = $"Server: {botHost}";
    }

    private async void LoginWithBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, string.Empty);
        try
        {
            await _companionClient.LoginWithBrowserAsync(OpenBrowserAsync).ConfigureAwait(true);
            LoginSucceeded?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Login failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private void SaveTokenButton_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Password.Trim();
        if (string.IsNullOrEmpty(token))
        {
            StatusText.Text = "Paste a token first.";
            return;
        }

        _companionClient.SetManualToken(token);
        LoginSucceeded?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private static Task OpenBrowserAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private void SetBusy(bool busy, string status)
    {
        LoginWithBrowserButton.IsEnabled = !busy;
        SaveTokenButton.IsEnabled = !busy;
        StatusText.Text = status;
    }
}
