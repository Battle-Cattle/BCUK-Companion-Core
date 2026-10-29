using System.Collections.Concurrent;
using System.Net;
using System.Windows.Forms;
using System.Windows.Threading;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.TrayApp.Updates;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

// Runs the real tray icon, windows and DPAPI token file, so only on Windows; AppPaths is process-wide.
[Collection(SharedShellState.Name)]
public sealed class TrayAppControllerTests : IDisposable
{
    private readonly TempAppFolder folder = new();
    private readonly ConcurrentQueue<string> messages = new();
    private readonly ConcurrentQueue<BotEventArgs> botEvents = new();
    private readonly RouteHttpHandler bot = new(request => request.RequestUri!.AbsolutePath == "/api/companion/token/revoke"
        ? RouteHttpHandler.Json("{}")
        : RouteHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
    private int shutdowns;
    private Action<string, string>? hostBalloon;
    private CompanionClient? readyClient;

    public void Dispose() => folder.Dispose();

    private CompanionTrayAppOptions Options(IReadOnlyList<TrayMenuItem>? menuItems = null, CompanionUpdateOptions? updates = null) => new()
    {
        DataFolderName = folder.Name,
        DisplayName = "Test Companion",
        AdditionalMenuItems = menuItems,
        Updates = updates,
        OnTrayIconReady = show => hostBalloon = show,
        OnClientReady = client => readyClient = client,
        OnBotEvent = botEvents.Enqueue,
    };

    private TrayAppController Controller(CompanionTrayAppOptions? options = null) => new(
        options ?? Options(),
        new TrayAppEnvironment(
            Dispatcher.CurrentDispatcher,
            () => Interlocked.Increment(ref shutdowns),
            (text, caption) => messages.Enqueue($"{caption}: {text}"),
            bot));

    private void SaveToken(string token) => new DpapiFileTokenStore(AppPaths.TokenFile).Save(token);

    private static void Click(TrayAppController controller, string label) =>
        controller.TrayIcon.NotifyIcon.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(i => i.Text == label).PerformClick();

    private static string Balloon(TrayAppController controller) =>
        $"{controller.TrayIcon.NotifyIcon.BalloonTipTitle}: {controller.TrayIcon.NotifyIcon.BalloonTipText}";

    [SkippableFact]
    public void RemoveLegacyInstallFiles_OnlyRunsForASingleFileBuild()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var install = Directory.CreateTempSubdirectory("bcuk-install-");
            try
            {
                Assert.False(TrayAppController.RemoveLegacyInstallFiles("C:\\app\\BCUKCompanion.TrayApp.Shell.dll", "C:\\app\\App.exe", install.FullName));
                Assert.False(TrayAppController.RemoveLegacyInstallFiles("", null, install.FullName));
                Assert.True(TrayAppController.RemoveLegacyInstallFiles("", Path.Combine(install.FullName, "App.exe"), install.FullName));
                Assert.False(TrayAppController.RemoveLegacyInstallFiles("", "App.exe", "\0 not a path"));
            }
            finally
            {
                install.Delete(recursive: true);
            }
        });
    }

    [SkippableFact]
    public void Start_LoggedOut_ShowsTheLoginWindowAndTheTrayIcon()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var controller = Controller();
            Assert.False(controller.ReportUnhandledError(new InvalidOperationException("too early")));

            Assert.True(controller.Start());

            Assert.True(controller.LoginWindow!.IsVisible);
            Assert.Equal("Test Companion — Login", controller.LoginWindow.Title);
            Assert.Equal("Test Companion", controller.TrayIcon.NotifyIcon.Text);
            Assert.Same(controller.Client, readyClient);
            Assert.False(controller.Client.IsLoggedIn);

            hostBalloon!("Host", "said hi");
            Assert.Equal("Host: said hi", Balloon(controller));

            Assert.True(controller.ReportUnhandledError(new InvalidOperationException("boom")));
            Assert.Equal("Test Companion hit an error: boom", Balloon(controller));
        });
    }

    [SkippableFact]
    public void Start_WhileAnotherInstanceRuns_SaysSoAndShutsDown()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var first = Controller();
            Assert.True(first.Start());

            using var second = Controller();
            Assert.False(second.Start());

            Assert.Equal(["Test Companion: Test Companion is already running — check your system tray."], messages);
            Assert.Equal(1, shutdowns);
        });
    }

    [SkippableFact]
    public void Dispose_ReleasesTheLockForTheNextInstance()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var first = Controller();
            Assert.True(first.Start());
            first.Dispose();

            using var next = Controller();
            Assert.True(next.Start());
        });
    }

    [SkippableFact]
    public void HostCallbacksThatThrow_DontStopStartup()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var options = new CompanionTrayAppOptions
            {
                DataFolderName = folder.Name,
                OnTrayIconReady = _ => throw new InvalidOperationException("host bug"),
                OnClientReady = _ => throw new InvalidOperationException("host bug"),
            };
            using var controller = Controller(options);

            Assert.True(controller.Start());
        });
    }

    [SkippableFact]
    public void Menu_OpensTheWindowsOnce_AndExitShutsDownWhenIdle()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            using var controller = Controller(Options(
                [new TrayMenuItem("Broken", () => throw new InvalidOperationException("host window failed"))],
                new CompanionUpdateOptions { RepoOwner = "owner", RepoName = "repo", CheckOnLaunch = false }));
            controller.Start();
            var login = controller.LoginWindow;

            Click(controller, "Login / Account");
            Assert.Same(login, controller.LoginWindow);

            Click(controller, "Settings");
            var settings = controller.SettingsWindow!;
            Assert.True(settings.IsVisible);
            Assert.Equal("Test Companion — Settings", settings.Title);
            Click(controller, "Settings");
            Assert.Same(settings, controller.SettingsWindow);

            Click(controller, "Broken");
            Assert.Equal("Broken failed: host window failed", Balloon(controller));
            Assert.Contains(
                controller.TrayIcon.NotifyIcon.ContextMenuStrip!.Items.Cast<ToolStripItem>(), i => i.Text == "Check for updates...");

            settings.Close();
            login!.Close();
            Assert.Null(controller.SettingsWindow);
            Assert.Null(controller.LoginWindow);

            Click(controller, "Exit");
            await WpfTest.WaitUntil(() => shutdowns == 1);
        });
    }

    [SkippableFact]
    public void SettingsLogOut_OpensTheLoginWindow()
    {
        WindowsOnly.Skip();
        SaveToken("token");
        WpfTest.Run(async () =>
        {
            using var controller = Controller();
            controller.Start();
            Assert.True(controller.Client.IsLoggedIn);
            Assert.Null(controller.LoginWindow);

            controller.ShowSettingsWindow();
            WpfTest.Click(controller.SettingsWindow!.LogOutButton);

            await WpfTest.WaitUntil(() => controller.LoginWindow is not null);
            Assert.False(controller.Client.IsLoggedIn);
        });
    }

    [SkippableFact]
    public void ARejectedToken_IsDiscardedAndTheLoginWindowOpened()
    {
        WindowsOnly.Skip();
        bot.Respond = _ => RouteHttpHandler.Json("{}", HttpStatusCode.Unauthorized);
        SaveToken("token");
        WpfTest.Run(async () =>
        {
            using var controller = Controller();
            controller.Start();

            await WpfTest.WaitUntil(() => controller.LoginWindow is not null);
            Assert.False(controller.Client.IsLoggedIn);
            Assert.Equal("Test Companion — AuthenticationFailed", controller.TrayIcon.NotifyIcon.Text);
        });
    }

    [SkippableFact]
    public void ATokenThatCantBeDeleted_IsReportedAndListeningStops()
    {
        WindowsOnly.Skip();
        bot.Respond = _ => RouteHttpHandler.Json("{}", HttpStatusCode.Unauthorized);
        SaveToken("token");
        // Held open without delete sharing, so discarding the rejected token fails.
        using var locked = new FileStream(AppPaths.TokenFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        WpfTest.Run(async () =>
        {
            using var controller = Controller();
            controller.Start();

            await WpfTest.WaitUntil(() => controller.LoginWindow is not null);
            Assert.StartsWith("Test Companion: Couldn't discard the rejected login:", Balloon(controller));
            Assert.True(controller.Client.IsLoggedIn);
        });
    }

    [SkippableFact]
    public void Start_ChecksForUpdatesWhenAskedTo()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            // The check runs in the background, and a launch check never prompts: for a repo with no
            // releases it finds nothing and stays silent.
            using var controller = Controller(Options(updates: new CompanionUpdateOptions
            {
                RepoOwner = "Battle-Cattle",
                RepoName = "BCUK-Companion-NoSuchRepo",
            }));

            Assert.True(controller.Start());
        });
    }

    [SkippableFact]
    public void ConnectionState_ShowsOnTheTrayAndInSettings()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var controller = Controller();
            controller.Start();

            controller.OnConnectionStateChanged(null, CompanionConnectionState.Connected);
            Assert.Equal("Test Companion — Connected", controller.TrayIcon.NotifyIcon.Text);
            controller.ShowSettingsWindow();
            Assert.Equal("Status: connected", controller.SettingsWindow!.ConnectionStatusText.Text);

            // A rejected token that's already been replaced leaves the Login window alone.
            controller.LoginWindow!.Close();
            controller.OnConnectionStateChanged(null, CompanionConnectionState.AuthenticationFailed);
            Assert.Null(controller.LoginWindow);
        });
    }

    [SkippableFact]
    public void ListenLoopFault_IsReported()
    {
        WindowsOnly.Skip();
        SaveToken("token");
        WpfTest.Run(() =>
        {
            using var controller = Controller();
            controller.Start();

            controller.OnListenLoopFaulted(null, new InvalidOperationException());
            Assert.Equal("Test Companion: Lost connection to the event stream — reconnecting…", Balloon(controller));

            controller.Client.Logout();
            controller.OnListenLoopFaulted(null, new InvalidOperationException());
        });
    }

    [SkippableFact]
    public void BotEvents_ReachTheHost()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            using var controller = Controller();
            controller.Start();

            controller.OnRedemptionReceived(null, new RedemptionEvent { Id = "r-1", RewardTitle = "Lights", UserName = "Viewer" });
            controller.OnAdvancedCommandReceived(null, new AdvancedCommandEvent { Id = "c-1", CommandName = "Hydrate", Source = "chat" });
            controller.OnActivityReceived(null, new CompanionActivityEvent { Type = "follow", Id = 1, DisplayName = "Viewer" });

            await WpfTest.WaitUntil(() => botEvents.Count == 3);
        });
    }

    [SkippableFact]
    public void SaveLastActivityEventId_KeepsTheNewestId()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var controller = Controller();
            controller.Start();

            controller.SaveLastActivityEventId("https://bot.example.com", 10);
            controller.SaveLastActivityEventId("https://bot.example.com", 5);
            Assert.Equal(10, AppSettings.Load().LastActivityEventIds["https://bot.example.com"]);

            File.Delete(AppPaths.SettingsFile);
            Directory.CreateDirectory(AppPaths.SettingsFile); // can't be written: best-effort, so ignored
            controller.SaveLastActivityEventId("https://bot.example.com", 11);
        });
    }

    [SkippableFact]
    public void ASavedActivityId_SeedsTheNextStart()
    {
        WindowsOnly.Skip();
        new AppSettings { LastActivityEventIds = { ["https://bot.example.com"] = 42 } }.Save();
        WpfTest.Run(() =>
        {
            using var controller = Controller();
            controller.Start();

            Assert.Equal(42, controller.Client.Events.LastActivityEventId);
        });
    }

    [SkippableFact]
    public void ChangingToAnotherServer_LogsOutAndSavesTheHost()
    {
        WindowsOnly.Skip();
        SaveToken("token");
        bot.Respond = request => request.RequestUri!.AbsolutePath == "/api/companion/token/revoke"
            ? RouteHttpHandler.Json("{}", HttpStatusCode.InternalServerError)
            : RouteHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        WpfTest.Run(async () =>
        {
            using var controller = Controller();
            controller.Start();
            var oldClient = controller.Client;
            controller.ShowSettingsWindow();
            controller.ShowLoginWindow();
            var login = controller.LoginWindow!;

            login.ServerHostBox.Text = "https://other.example";
            WpfTest.Click(login.ChangeServerButton);

            Assert.NotSame(oldClient, controller.Client);
            Assert.False(controller.Client.IsLoggedIn);
            Assert.Equal("https://other.example", AppSettings.Load().BotHost);
            Assert.Equal("https://other.example", controller.SettingsWindow!.BotHostText.Text);
            Assert.Equal("Test Companion — Disconnected", controller.TrayIcon.NotifyIcon.Text);
            await WpfTest.WaitUntil(() => Balloon(controller).Contains("didn't confirm the old login was revoked"));

            // The same host again changes nothing.
            var current = controller.Client;
            WpfTest.Click(login.ChangeServerButton);
            Assert.Same(current, controller.Client);
        });
    }

    [SkippableFact]
    public void ChangingOnlyThePath_StaysLoggedIn()
    {
        WindowsOnly.Skip();
        SaveToken("token");
        WpfTest.Run(() =>
        {
            using var controller = Controller();
            controller.Start();
            var oldClient = controller.Client;
            controller.ShowLoginWindow();
            var login = controller.LoginWindow!;

            login.ServerHostBox.Text = "https://bot.example.com/companion";
            WpfTest.Click(login.ChangeServerButton);

            Assert.NotSame(oldClient, controller.Client);
            Assert.True(controller.Client.IsLoggedIn);
            Assert.Equal("Server updated. You're still logged in to this server.", login.ServerStatusText.Text);
        });
    }
}
