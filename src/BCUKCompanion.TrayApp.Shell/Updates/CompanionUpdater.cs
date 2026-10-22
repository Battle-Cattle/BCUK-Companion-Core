using System.Reflection;
using BCUKCompanion.Core.Updates;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>
/// The user-facing update flow on top of <see cref="GitHubReleaseUpdateChecker"/>: a silent check
/// at launch that installs any newer release automatically, and a "Check for updates..." tray menu
/// item that reports the result and asks before installing. Either way, installing shuts the app
/// down so the update script can swap the files and relaunch it.
/// </summary>
internal sealed class CompanionUpdater
{
    private readonly GitHubReleaseUpdateChecker checker;
    private readonly Func<AvailableUpdate, Task> install;
    private readonly string displayName;
    private readonly IUpdateUi ui;

    // Interlocked-guarded: 1 while a check/install is in flight, so a menu click during the
    // launch check (or a double click) doesn't start a second download.
    private int busy;

    /// <summary>Creates an updater for the running app, whose version comes from the host's entry assembly.</summary>
    /// <param name="options">Which GitHub repository's releases to update from.</param>
    /// <param name="displayName">The app's name, for messages.</param>
    /// <param name="dataFolderName">The app's data folder, where the update is staged.</param>
    /// <param name="showBalloon">Shows a tray balloon (title, text).</param>
    public CompanionUpdater(CompanionUpdateOptions options, string displayName, string dataFolderName, Action<string, string> showBalloon)
        : this(CreateChecker(options), displayName, dataFolderName, showBalloon)
    {
    }

    /// <summary>Creates an updater that installs with an <see cref="UpdateInstaller"/> and prompts with WPF message boxes.</summary>
    /// <param name="checker">Finds and downloads releases.</param>
    /// <param name="displayName">The app's name, for messages.</param>
    /// <param name="dataFolderName">The app's data folder, where the update is staged.</param>
    /// <param name="showBalloon">Shows a tray balloon (title, text).</param>
    private CompanionUpdater(GitHubReleaseUpdateChecker checker, string displayName, string dataFolderName, Action<string, string> showBalloon)
        : this(
            checker,
            update => new UpdateInstaller(checker, dataFolderName).PrepareAndLaunchAsync(update),
            displayName,
            new WpfUpdateUi($"{displayName} updates", showBalloon))
    {
    }

    /// <summary>Creates an updater with the given installer and UI, so tests can stand in for both.</summary>
    /// <param name="checker">Finds releases; its <see cref="GitHubReleaseUpdateChecker.CurrentVersion"/> decides whether this build updates itself.</param>
    /// <param name="install">Downloads a release and starts the script that swaps it in; throws if it can't.</param>
    /// <param name="displayName">The app's name, for messages.</param>
    /// <param name="ui">Shows messages and balloons, asks questions and shuts the app down.</param>
    internal CompanionUpdater(GitHubReleaseUpdateChecker checker, Func<AvailableUpdate, Task> install, string displayName, IUpdateUi ui)
    {
        this.checker = checker;
        this.install = install;
        this.displayName = displayName;
        this.ui = ui;
    }

    /// <summary>The release checker for the running app, whose version comes from the host's entry assembly.</summary>
    /// <param name="options">Which GitHub repository's releases to update from.</param>
    /// <returns>A checker for that repository's releases.</returns>
    private static GitHubReleaseUpdateChecker CreateChecker(CompanionUpdateOptions options)
    {
        // The host's exe, not this library, carries the version the release workflow stamps.
        var version = AppVersion.FromAssembly(Assembly.GetEntryAssembly() ?? typeof(CompanionUpdater).Assembly);
        return new GitHubReleaseUpdateChecker(options.RepoOwner, options.RepoName, version, assetSuffix: options.AssetSuffix);
    }

    /// <summary>The "Check for updates..." tray menu item, which checks now and reports the result.</summary>
    /// <returns>A new menu item that starts an interactive check when clicked.</returns>
    public TrayMenuItem CreateMenuItem() => new("Check for updates...", () => _ = RunAsync(interactive: true));

    /// <summary>
    /// Starts the background launch check. Call on the UI thread. Local/dev builds skip it so they
    /// never replace themselves with the published release.
    /// </summary>
    public void CheckOnLaunch()
    {
        if (checker.CurrentVersion.IsReleaseBuild)
        {
            _ = RunAsync(interactive: false);
        }
    }

    /// <summary>
    /// Checks for an update and installs it (after asking, when <paramref name="interactive"/>),
    /// unless a check is already running. Awaits resume on the UI thread (no ConfigureAwait(false)),
    /// so message boxes and Shutdown run where WPF expects them.
    /// </summary>
    /// <param name="interactive">True for a menu click, which reports every outcome; false for the silent launch check.</param>
    /// <returns>A task that completes once the check (and any install) is done; it never faults.</returns>
    internal async Task RunAsync(bool interactive)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            if (interactive)
            {
                ui.ShowMessage("An update check is already in progress.");
            }
            return;
        }

        try
        {
            var update = await FindUpdateAsync(interactive);
            if (update is not null && (!interactive || ConfirmInstall(update)))
            {
                await InstallAsync(update, interactive);
            }
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// <summary>
    /// Returns the update to install, or <c>null</c> after telling an interactive user why not.
    /// The launch check stays silent: being offline at launch is normal.
    /// </summary>
    /// <param name="interactive">True to show a message box when there's nothing to install (up to date, a dev build, or the check failed).</param>
    /// <returns>A task that completes with the newer release, or null when there isn't one or the check failed; it never faults.</returns>
    private async Task<AvailableUpdate?> FindUpdateAsync(bool interactive)
    {
        AvailableUpdate? update = null;
        string? message;
        if (!checker.CurrentVersion.IsReleaseBuild)
        {
            message = "This is a development build, so it doesn't update itself. Download releases from GitHub instead.";
        }
        else
        {
            try
            {
                update = await checker.CheckForUpdateAsync();
                message = update is null ? $"You're on the latest version (v{checker.CurrentVersion})." : null;
            }
            catch (Exception ex)
            {
                message = $"Couldn't check for updates:\n{ex.Message}";
            }
        }

        if (interactive && message is not null)
        {
            ui.ShowMessage(message);
        }
        return update;
    }

    /// <summary>Asks the user whether to install <paramref name="update"/> now.</summary>
    /// <param name="update">The newer release found by the check.</param>
    /// <returns>True when the user clicked Yes; otherwise false.</returns>
    private bool ConfirmInstall(AvailableUpdate update) => ui.Confirm(
        $"Version {update.Version} is available (you have {checker.CurrentVersion}).\n\nInstall it now? The app will restart.");

    /// <summary>Downloads and verifies <paramref name="update"/>, launches the script that swaps it in, then shuts the app down so the script can replace it.</summary>
    /// <param name="update">The release to install.</param>
    /// <param name="interactive">True when started from the menu, so a failure gets a message box rather than a balloon.</param>
    /// <returns>A task that completes once shutdown is queued, or once a failure has been reported; it never faults.</returns>
    private async Task InstallAsync(AvailableUpdate update, bool interactive)
    {
        ui.ShowBalloon($"Updating {displayName}", $"Downloading v{update.Version}. The app will restart when it's ready.");
        try
        {
            await install(update);
        }
        catch (Exception ex)
        {
            ReportInstallFailure(update, ex, interactive);
            return;
        }

        ui.Shutdown();
    }

    /// <summary>Tells the user an install failed: a balloon for the launch check, or a message box offering the download page for a menu click.</summary>
    /// <param name="update">The release that failed to install.</param>
    /// <param name="ex">Why it failed; an <see cref="UnauthorizedAccessException"/> adds advice to move the app or download manually.</param>
    /// <param name="interactive">True for a menu click (message box); false for the launch check (balloon).</param>
    private void ReportInstallFailure(AvailableUpdate update, Exception ex, bool interactive)
    {
        string reason = ex is UnauthorizedAccessException
            ? $"{ex.Message} Move the app to a folder you can write to, or download v{update.Version} manually."
            : ex.Message;

        if (!interactive)
        {
            ui.ShowBalloon("Update failed", $"Couldn't install v{update.Version}: {reason}");
            return;
        }

        if (ui.ConfirmWarning($"Couldn't install v{update.Version}:\n{reason}\n\nOpen the download page?"))
        {
            // GitHubReleaseUpdateChecker only ever returns an https release page URL.
            ui.OpenUrl(update.ReleasePageUrl);
        }
    }
}
