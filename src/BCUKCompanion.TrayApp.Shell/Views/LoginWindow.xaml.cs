using System.Diagnostics;
using System.Windows;
using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Views;

public partial class LoginWindow : Window
{
    private CompanionClient _companionClient;

    public event EventHandler? LoginSucceeded;

    /// <summary>Raised when the user changes the server URL from this window, before any login attempt.</summary>
    public event EventHandler<string>? ServerHostChanged;

    public LoginWindow(CompanionClient companionClient, string botHost)
    {
        _companionClient = companionClient;
        InitializeComponent();
        ServerHostBox.Text = botHost;
    }

    /// <summary>Re-points this still-open window at a companion client created for a newly saved bot host.</summary>
    internal void UpdateCompanionClient(CompanionClient companionClient, string botHost)
    {
        _companionClient = companionClient;
        ServerHostBox.Text = botHost;
    }

    private void ChangeServerButton_Click(object sender, RoutedEventArgs e)
    {
        string newBotHost = ServerHostBox.Text.Trim();
        if (!Uri.TryCreate(newBotHost, UriKind.Absolute, out Uri? parsedHost)
            || (parsedHost.Scheme != Uri.UriSchemeHttp && parsedHost.Scheme != Uri.UriSchemeHttps))
        {
            ServerStatusText.Text = "Enter a valid http(s) server URL.";
            return;
        }

        ServerHostChanged?.Invoke(this, newBotHost);
        ServerStatusText.Text = "Server updated. No connection has been made yet.";
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

        try
        {
            _companionClient.SetManualToken(token);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't save token: {ex.Message}";
            return;
        }

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
