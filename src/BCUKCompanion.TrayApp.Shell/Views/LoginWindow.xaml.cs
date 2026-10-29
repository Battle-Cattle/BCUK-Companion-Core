using System.Diagnostics;
using System.Windows;
using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp.Views;

/// <summary>Logs in to the bot, through the browser or with a pasted token, and lets the user change the server first.</summary>
public partial class LoginWindow : Window
{
    private CompanionClient _companionClient;
    private bool _isBusy;

    /// <summary>Raised once a login saved a token, just before the window closes.</summary>
    public event EventHandler? LoginSucceeded;

    /// <summary>Raised when the user changes the server URL from this window, before any login attempt.</summary>
    public event EventHandler<string>? ServerHostChanged;

    /// <summary>Opens the login page in the browser; tests replace it to stand in for the browser.</summary>
    internal Func<Uri, Task> OpenBrowser { get; set; } = OpenBrowserAsync;

    /// <summary>Creates the window for <paramref name="companionClient"/>, showing <paramref name="botHost"/> as the server.</summary>
    /// <param name="companionClient">The client to log in with.</param>
    /// <param name="botHost">The current server URL.</param>
    public LoginWindow(CompanionClient companionClient, string botHost)
    {
        _companionClient = companionClient;
        InitializeComponent();
        ServerHostBox.Text = botHost;
    }

    /// <summary>Re-points this still-open window at a companion client created for a newly saved bot host.</summary>
    /// <param name="companionClient">The client for the new bot host, used for the next login attempt.</param>
    /// <param name="botHost">The new server URL to show.</param>
    internal void UpdateCompanionClient(CompanionClient companionClient, string botHost)
    {
        _companionClient = companionClient;
        ServerHostBox.Text = botHost;
    }

    /// <summary>Validates and applies the typed server URL (raising <see cref="ServerHostChanged"/>), unless a login is in progress.</summary>
    /// <param name="sender">The server field's Change button (unused).</param>
    /// <param name="e">Click event data (unused).</param>
    private void ChangeServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            ServerStatusText.Text = "Wait for the current login attempt to finish before changing server.";
            return;
        }

        string newBotHost = ServerHostBox.Text.Trim();
        if (!Uri.TryCreate(newBotHost, UriKind.Absolute, out Uri? parsedHost)
            || parsedHost.Scheme != Uri.UriSchemeHttps)
        {
            ServerStatusText.Text = "Enter a valid https server URL.";
            return;
        }

        try
        {
            ServerHostChanged?.Invoke(this, newBotHost);
        }
        catch (Exception ex)
        {
            ServerStatusText.Text = $"Couldn't update server: {ex.Message}";
            return;
        }

        // A change that keeps the same server (e.g. only the path) keeps the session; any other logs out.
        ServerStatusText.Text = _companionClient.IsLoggedIn
            ? "Server updated. You're still logged in to this server."
            : "Server updated. No connection has been made yet — log in to this server.";
    }

    /// <summary>Runs the browser login and closes the window on success, or shows why it failed.</summary>
    /// <param name="sender">The "Login with Discord (opens browser)" button (unused).</param>
    /// <param name="e">Click event data (unused).</param>
    private async void LoginWithBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, string.Empty);
        try
        {
            await _companionClient.LoginWithBrowserAsync(OpenBrowser).ConfigureAwait(true);
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

    /// <summary>Saves the pasted token and closes the window, or shows why it couldn't.</summary>
    /// <param name="sender">The Save Token button (unused).</param>
    /// <param name="e">Click event data (unused).</param>
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

    /// <summary>Opens the login URL in the default browser, refusing anything but an http(s) URL.</summary>
    /// <param name="uri">The bot's login page URL.</param>
    /// <returns>An already-completed task once the browser has been started; throws <see cref="InvalidOperationException"/> for a non-web URL.</returns>
    internal static Task OpenBrowserAsync(Uri uri)
    {
        // UseShellExecute launches whatever the URL points at, so only ever hand it a web URL —
        // never a file: path or other scheme a mis-set bot host could produce.
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException($"Refusing to open a non-web login URL ({uri.Scheme}:). Check the server URL.");
        }

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// <summary>Enables or disables the window's controls around a login attempt, and shows <paramref name="status"/>.</summary>
    /// <param name="busy">True to disable the login and server controls while an attempt runs; false to re-enable them.</param>
    /// <param name="status">The text for the status line; empty to clear it.</param>
    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        LoginWithBrowserButton.IsEnabled = !busy;
        SaveTokenButton.IsEnabled = !busy;
        ServerHostBox.IsEnabled = !busy;
        ChangeServerButton.IsEnabled = !busy;
        StatusText.Text = status;
    }
}
