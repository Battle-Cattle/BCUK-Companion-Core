using System.Net;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.TrayApp.Services;
using BCUKCompanion.TrayApp.Views;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Views;

// Saving writes AppPaths' settings file and the autostart registry value.
[Collection(SharedShellState.Name)]
public sealed class SettingsWindowTests : IDisposable
{
    private readonly TempAppFolder folder = new();

    public void Dispose() => folder.Dispose();

    /// <summary>A token store whose file can't be deleted.</summary>
    private sealed class LockedTokenStore : ITokenStore
    {
        public string? Load() => "token";

        public void Save(string newToken)
        {
        }

        public void Clear() => throw new IOException("token file locked");
    }

    private static CompanionClient Client(ITokenStore store, HttpStatusCode revokeStatus = HttpStatusCode.OK) =>
        new(new Uri("https://bot.example"), store, new HttpClient(new RouteHttpHandler(_ => RouteHttpHandler.Json("{}", revokeStatus))));

    [SkippableFact]
    public void ShowsTheHostAndConnection()
    {
        WindowsOnly.Skip();
        using var client = Client(new MemoryTokenStore());
        WpfTest.Run(() =>
        {
            var window = new SettingsWindow(client, new AppSettings { BotHost = "https://bot.example" }, isConnected: true);
            Assert.Equal("https://bot.example", window.BotHostText.Text);
            Assert.Equal("Status: connected", window.ConnectionStatusText.Text);
            Assert.False(window.StartWithWindowsCheckBox.IsChecked);

            window.RefreshBotHost("https://other.example");
            Assert.Equal("https://other.example", window.BotHostText.Text);

            Assert.Equal("Status: not connected", new SettingsWindow(client, new AppSettings(), isConnected: false).ConnectionStatusText.Text);
        });
    }

    [SkippableFact]
    public void Save_StoresTheSettingAndTheRunEntry()
    {
        WindowsOnly.Skip();
        using var client = Client(new MemoryTokenStore());
        var settings = new AppSettings();
        WpfTest.Run(() =>
        {
            var window = new SettingsWindow(client, settings, isConnected: false);

            window.StartWithWindowsCheckBox.IsChecked = true;
            WpfTest.Click(window.SaveButton);

            Assert.Equal("Saved.", window.StatusText.Text);
        });

        Assert.True(settings.StartWithWindows);
        Assert.True(AppSettings.Load().StartWithWindows);
        Assert.True(AutoStartService.IsEnabled());
    }

    [SkippableFact]
    public void Save_ReportsASettingsFileThatCantBeWritten()
    {
        WindowsOnly.Skip();
        using var client = Client(new MemoryTokenStore());
        Directory.CreateDirectory(AppPaths.SettingsFile); // a folder where the file should go
        WpfTest.Run(() =>
        {
            var window = new SettingsWindow(client, new AppSettings(), isConnected: false);

            WpfTest.Click(window.SaveButton);

            Assert.StartsWith("Couldn't save:", window.StatusText.Text);
        });
    }

    [SkippableFact]
    public void LogOut_RevokesTheTokenAndCloses()
    {
        WindowsOnly.Skip();
        var store = new MemoryTokenStore("token");
        using var client = Client(store);
        WpfTest.Run(async () =>
        {
            var window = new SettingsWindow(new CompanionClient(new Uri("https://unused.example"), new MemoryTokenStore()), new AppSettings(), false)
            {
                ShowActivated = false,
            };
            window.UpdateCompanionClient(client);
            var loggedOut = 0;
            window.LoggedOut += (_, _) => loggedOut++;
            window.Show();

            WpfTest.Click(window.LogOutButton);
            await WpfTest.WaitUntil(() => loggedOut == 1);

            Assert.False(window.IsVisible);
            Assert.Null(store.Load());
        });
    }

    [SkippableFact]
    public void LogOut_SaysWhenTheBotDidntConfirmTheRevoke()
    {
        WindowsOnly.Skip();
        using var client = Client(new MemoryTokenStore("token"), HttpStatusCode.InternalServerError);
        WpfTest.Run(async () =>
        {
            var window = new SettingsWindow(client, new AppSettings(), false) { ShowActivated = false };
            var loggedOut = 0;
            window.LoggedOut += (_, _) => loggedOut++;
            window.Show();

            WpfTest.Click(window.LogOutButton);
            await WpfTest.WaitUntil(() => window.IsEnabled && loggedOut == 1);

            Assert.True(window.IsVisible);
            Assert.False(window.LogOutButton.IsEnabled);
            Assert.StartsWith("Logged out on this PC, but the bot didn't confirm", window.StatusText.Text);
            WpfTest.Click(window.CloseButton);
            Assert.False(window.IsVisible);
        });
    }

    [SkippableFact]
    public void LogOut_ReportsATokenThatCantBeDeleted()
    {
        WindowsOnly.Skip();
        using var client = Client(new LockedTokenStore());
        WpfTest.Run(() =>
        {
            var window = new SettingsWindow(client, new AppSettings(), false);
            var loggedOut = 0;
            window.LoggedOut += (_, _) => loggedOut++;

            WpfTest.Click(window.LogOutButton);

            Assert.Equal("Couldn't log out: token file locked", window.StatusText.Text);
            Assert.True(window.IsEnabled);
            Assert.Equal(0, loggedOut);
        });
    }
}
