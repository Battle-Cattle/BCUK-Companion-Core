using System.Net;
using BCUKCompanion.Core.Updates;
using BCUKCompanion.TrayApp.Updates;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Updates;

public sealed class CompanionUpdaterTests
{
    private static readonly AppVersion V100 = new(new Version(1, 0, 0), false);
    private static readonly AppVersion DevBuild = new(new Version(0, 0, 0), false);

    private readonly FakeUpdateUi ui = new();
    private readonly List<AvailableUpdate> installs = [];

    private static string ReleaseJson(string tag) =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/Battle-Cattle/App/releases/tag/{{tag}}",
          "assets": [{"name":"App-{{tag}}-win-x64.zip","browser_download_url":"https://example.test/App-{{tag}}-win-x64.zip"}]
        }
        """;

    private CompanionUpdater CreateUpdater(HttpMessageHandler handler, AppVersion? current = null, Exception? installError = null)
    {
        var checker = new GitHubReleaseUpdateChecker("Battle-Cattle", "App", current ?? V100, new HttpClient(handler));
        return new CompanionUpdater(
            checker,
            update =>
            {
                installs.Add(update);
                return installError is null ? Task.CompletedTask : Task.FromException(installError);
            },
            "App",
            ui);
    }

    [Fact]
    public async Task RunAsync_DevBuild_ExplainsWhyWithoutChecking()
    {
        var handler = new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0"));

        await CreateUpdater(handler, DevBuild).RunAsync(interactive: true);

        Assert.Equal(0, handler.Requests);
        Assert.Contains("development build", Assert.Single(ui.Messages));
        Assert.Empty(installs);
    }

    [Fact]
    public void CheckOnLaunch_DevBuild_DoesNothing()
    {
        var handler = new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0"));

        CreateUpdater(handler, DevBuild).CheckOnLaunch();

        Assert.Equal(0, handler.Requests);
        Assert.Empty(ui.Messages);
    }

    [Fact]
    public async Task CheckOnLaunch_ReleaseBuild_InstallsWithoutAsking()
    {
        var handler = new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0"));

        CreateUpdater(handler).CheckOnLaunch();

        await ui.ShutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new Version(1, 1, 0), Assert.Single(installs).Version);
        Assert.Empty(ui.Questions);
    }

    [Fact]
    public async Task RunAsync_UpToDate_TellsAnInteractiveUser()
    {
        await CreateUpdater(new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.0.0"))).RunAsync(interactive: true);

        Assert.Equal("You're on the latest version (v1.0.0).", Assert.Single(ui.Messages));
        Assert.Empty(installs);
    }

    [Fact]
    public async Task RunAsync_UpToDate_StaysQuietAtLaunch()
    {
        await CreateUpdater(new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.0.0"))).RunAsync(interactive: false);

        Assert.Empty(ui.Messages);
        Assert.Empty(ui.Balloons);
    }

    [Fact]
    public async Task RunAsync_CheckFails_TellsAnInteractiveUser()
    {
        await CreateUpdater(new GatedHandler(HttpStatusCode.InternalServerError, "{}")).RunAsync(interactive: true);

        Assert.StartsWith("Couldn't check for updates:", Assert.Single(ui.Messages));
        Assert.Empty(installs);
    }

    [Fact]
    public async Task RunAsync_CheckFails_StaysQuietAtLaunch()
    {
        await CreateUpdater(new GatedHandler(HttpStatusCode.InternalServerError, "{}")).RunAsync(interactive: false);

        Assert.Empty(ui.Messages);
        Assert.Empty(ui.Balloons);
    }

    [Fact]
    public async Task RunAsync_UpdateAtLaunch_InstallsWithoutAskingThenShutsDown()
    {
        await CreateUpdater(new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0"))).RunAsync(interactive: false);

        Assert.Equal(new Version(1, 1, 0), Assert.Single(installs).Version);
        Assert.Empty(ui.Questions);
        Assert.Equal("Updating App", Assert.Single(ui.Balloons).Title);
        Assert.Equal(1, ui.Shutdowns);
    }

    [Fact]
    public async Task RunAsync_InteractiveUpdateDeclined_DoesNotInstall()
    {
        ui.ConfirmAnswer = false;

        await CreateUpdater(new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0"))).RunAsync(interactive: true);

        Assert.Contains("Version 1.1.0 is available (you have 1.0.0)", Assert.Single(ui.Questions));
        Assert.Empty(installs);
        Assert.Equal(0, ui.Shutdowns);
    }

    [Fact]
    public async Task RunAsync_InteractiveUpdateAccepted_InstallsThenShutsDown()
    {
        ui.ConfirmAnswer = true;

        await CreateUpdater(new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0"))).RunAsync(interactive: true);

        Assert.Single(installs);
        Assert.Equal(1, ui.Shutdowns);
    }

    [Fact]
    public async Task RunAsync_InstallFailsAtLaunch_ReportsItInABalloonAndKeepsRunning()
    {
        var updater = CreateUpdater(
            new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0")), installError: new InvalidDataException("Bad zip."));

        await updater.RunAsync(interactive: false);

        Assert.Equal(("Update failed", "Couldn't install v1.1.0: Bad zip."), ui.Balloons[^1]);
        Assert.Empty(ui.Warnings);
        Assert.Equal(0, ui.Shutdowns);
    }

    [Fact]
    public async Task RunAsync_InteractiveInstallFails_OffersTheDownloadPage()
    {
        ui.ConfirmAnswer = true;
        ui.WarningAnswer = true;
        var updater = CreateUpdater(
            new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0")), installError: new InvalidDataException("Bad zip."));

        await updater.RunAsync(interactive: true);

        Assert.Equal("Couldn't install v1.1.0:\nBad zip.\n\nOpen the download page?", Assert.Single(ui.Warnings));
        Assert.Equal(new Uri("https://github.com/Battle-Cattle/App/releases/tag/v1.1.0"), Assert.Single(ui.OpenedUrls));
        Assert.Equal(0, ui.Shutdowns);
    }

    [Fact]
    public async Task RunAsync_InteractiveInstallFails_DownloadPageDeclined_OpensNothing()
    {
        ui.ConfirmAnswer = true;
        ui.WarningAnswer = false;
        var updater = CreateUpdater(
            new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0")), installError: new InvalidDataException("Bad zip."));

        await updater.RunAsync(interactive: true);

        Assert.Single(ui.Warnings);
        Assert.Empty(ui.OpenedUrls);
    }

    [Fact]
    public async Task RunAsync_InstallFolderNotWritable_AdvisesMovingTheApp()
    {
        var updater = CreateUpdater(
            new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0")),
            installError: new UnauthorizedAccessException("Can't write to the install folder."));

        await updater.RunAsync(interactive: false);

        Assert.Equal(
            "Couldn't install v1.1.0: Can't write to the install folder. Move the app to a folder you can write to, or download v1.1.0 manually.",
            ui.Balloons[^1].Text);
    }

    [Fact]
    public async Task RunAsync_WhileACheckIsRunning_DoesNotStartAnother()
    {
        var handler = new GatedHandler(HttpStatusCode.OK, ReleaseJson("v1.0.0")) { Gate = new() };
        var updater = CreateUpdater(handler);

        Task first = updater.RunAsync(interactive: false);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Without the guard these would wait on the gated request; time out instead of hanging.
        await updater.RunAsync(interactive: true).WaitAsync(TimeSpan.FromSeconds(10));
        await updater.RunAsync(interactive: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("An update check is already in progress.", Assert.Single(ui.Messages));
        handler.Gate.SetResult();
        await first;
        Assert.Equal(1, handler.Requests);

        await updater.RunAsync(interactive: false);
        Assert.Equal(2, handler.Requests);
    }

    private sealed class GatedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int requests;

        public TaskCompletionSource? Gate { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests => Volatile.Read(ref requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            Entered.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class FakeUpdateUi : IUpdateUi
    {
        public List<string> Messages { get; } = [];

        public List<string> Questions { get; } = [];

        public List<string> Warnings { get; } = [];

        public List<(string Title, string Text)> Balloons { get; } = [];

        public List<Uri> OpenedUrls { get; } = [];

        public int Shutdowns { get; private set; }

        public TaskCompletionSource ShutdownRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ConfirmAnswer { get; set; }

        public bool WarningAnswer { get; set; }

        public void ShowMessage(string text) => Messages.Add(text);

        public bool Confirm(string text)
        {
            Questions.Add(text);
            return ConfirmAnswer;
        }

        public bool ConfirmWarning(string text)
        {
            Warnings.Add(text);
            return WarningAnswer;
        }

        public void ShowBalloon(string title, string text) => Balloons.Add((title, text));

        public void OpenUrl(Uri url) => OpenedUrls.Add(url);

        public void Shutdown()
        {
            Shutdowns++;
            ShutdownRequested.TrySetResult();
        }
    }
}
