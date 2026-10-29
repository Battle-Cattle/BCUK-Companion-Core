using BCUKCompanion.Core;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.TrayApp.Views;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Views;

public sealed class LoginWindowTests
{
    private const string BotHost = "https://bot.example";

    /// <summary>A token store whose saves fail, as a locked token file would.</summary>
    private sealed class FailingTokenStore : ITokenStore
    {
        public string? Load() => null;

        public void Save(string newToken) => throw new IOException("token file locked");

        public void Clear()
        {
        }
    }

    private static CompanionClient Client(ITokenStore? store = null, HttpMessageHandler? handler = null) =>
        new(new Uri(BotHost), store ?? new MemoryTokenStore(), handler is null ? null : new HttpClient(handler));

    [SkippableFact]
    public void OpenBrowser_RefusesANonWebUrl()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoginWindow.OpenBrowserAsync(new Uri("file:///c:/windows/system32/calc.exe")));

            Assert.Contains("non-web login URL (file:)", ex.Message);
        });
    }

    [SkippableFact]
    public void ChangeServer_RejectsAnythingButAnHttpsUrl()
    {
        WindowsOnly.Skip();
        using var client = Client();
        WpfTest.Run(() =>
        {
            var window = new LoginWindow(client, BotHost);
            var raised = 0;
            window.ServerHostChanged += (_, _) => raised++;

            Assert.Equal(BotHost, window.ServerHostBox.Text);
            window.ServerHostBox.Text = "http://bot.example";
            WpfTest.Click(window.ChangeServerButton);

            Assert.Equal("Enter a valid https server URL.", window.ServerStatusText.Text);
            Assert.Equal(0, raised);
        });
    }

    [SkippableFact]
    public void ChangeServer_ReportsTheNewHost_AndWhetherStillLoggedIn()
    {
        WindowsOnly.Skip();
        using var loggedOut = Client();
        using var loggedIn = Client(new MemoryTokenStore("token"));
        WpfTest.Run(() =>
        {
            var window = new LoginWindow(loggedOut, BotHost);
            var hosts = new List<string>();
            window.ServerHostChanged += (_, host) => hosts.Add(host);

            window.ServerHostBox.Text = "  https://other.example  ";
            WpfTest.Click(window.ChangeServerButton);
            Assert.Equal(["https://other.example"], hosts);
            Assert.StartsWith("Server updated. No connection", window.ServerStatusText.Text);

            window.UpdateCompanionClient(loggedIn, "https://bot.example/path");
            Assert.Equal("https://bot.example/path", window.ServerHostBox.Text);
            WpfTest.Click(window.ChangeServerButton);
            Assert.Equal("Server updated. You're still logged in to this server.", window.ServerStatusText.Text);
        });
    }

    [SkippableFact]
    public void ChangeServer_ReportsAHandlerThatThrows()
    {
        WindowsOnly.Skip();
        using var client = Client();
        WpfTest.Run(() =>
        {
            var window = new LoginWindow(client, BotHost);
            window.ServerHostChanged += (_, _) => throw new IOException("settings locked");

            WpfTest.Click(window.ChangeServerButton);

            Assert.Equal("Couldn't update server: settings locked", window.ServerStatusText.Text);
        });
    }

    [SkippableFact]
    public void SaveToken_NeedsAToken()
    {
        WindowsOnly.Skip();
        using var client = Client();
        WpfTest.Run(() =>
        {
            var window = new LoginWindow(client, BotHost);

            WpfTest.Click(window.SaveTokenButton);

            Assert.Equal("Paste a token first.", window.StatusText.Text);
        });
    }

    [SkippableFact]
    public void SaveToken_SavesItAndCloses()
    {
        WindowsOnly.Skip();
        var store = new MemoryTokenStore();
        using var client = Client(store);
        WpfTest.Run(() =>
        {
            var window = new LoginWindow(client, BotHost) { ShowActivated = false };
            var succeeded = 0;
            var closed = false;
            window.LoginSucceeded += (_, _) => succeeded++;
            window.Closed += (_, _) => closed = true;
            window.Show();

            window.TokenBox.Password = "  pasted-token  ";
            WpfTest.Click(window.SaveTokenButton);

            Assert.Equal("pasted-token", store.Load());
            Assert.Equal(1, succeeded);
            Assert.True(closed);
        });
    }

    [SkippableFact]
    public void SaveToken_ReportsAStoreThatFails()
    {
        WindowsOnly.Skip();
        using var client = Client(new FailingTokenStore());
        WpfTest.Run(() =>
        {
            var window = new LoginWindow(client, BotHost);

            window.TokenBox.Password = "pasted-token";
            WpfTest.Click(window.SaveTokenButton);

            Assert.Equal("Couldn't save token: token file locked", window.StatusText.Text);
        });
    }

    [SkippableFact]
    public void LoginWithBrowser_ReportsAFailure_AndReenablesTheForm()
    {
        WindowsOnly.Skip();
        using var client = Client();
        WpfTest.Run(async () =>
        {
            var window = new LoginWindow(client, BotHost) { OpenBrowser = _ => throw new InvalidOperationException("no browser") };

            WpfTest.Click(window.LoginWithBrowserButton);
            await WpfTest.WaitUntil(() => window.LoginWithBrowserButton.IsEnabled);

            Assert.Equal("Login failed: no browser", window.StatusText.Text);
            Assert.True(window.SaveTokenButton.IsEnabled);
        });
    }

    [SkippableFact]
    public void LoginWithBrowser_SavesTheIssuedTokenAndCloses()
    {
        WindowsOnly.Skip();
        var store = new MemoryTokenStore();
        var bot = new RouteHttpHandler(_ => RouteHttpHandler.Json("""{"token":"issued-token"}"""));
        using var client = Client(store, bot);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        WpfTest.Run(async () =>
        {
            var window = new LoginWindow(client, BotHost) { ShowActivated = false };
            var succeeded = 0;
            var busyWhileWaiting = false;
            window.LoginSucceeded += (_, _) => succeeded++;
            window.OpenBrowser = loginUrl =>
            {
                busyWhileWaiting = !window.SaveTokenButton.IsEnabled;
                var query = System.Web.HttpUtility.ParseQueryString(loginUrl.Query);
                var state = query["state"] ?? throw new InvalidOperationException($"The login URL has no state: {loginUrl}");
                _ = Task.Run(() => browser.GetAsync($"{query["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(state)}"));
                return Task.CompletedTask;
            };
            window.Show();

            WpfTest.Click(window.LoginWithBrowserButton);
            WpfTest.Click(window.ChangeServerButton);
            Assert.StartsWith("Wait for the current login attempt", window.ServerStatusText.Text);
            await WpfTest.WaitUntil(() => succeeded == 1);

            Assert.True(busyWhileWaiting);
            Assert.Equal("issued-token", store.Load());
            Assert.False(window.IsVisible);
        });
    }
}
