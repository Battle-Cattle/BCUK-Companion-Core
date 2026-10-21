using System.Diagnostics;
using System.Reflection;
using System.Windows;
using BCUKCompanion.Core.Updates;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

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
    private readonly UpdateInstaller installer;
    private readonly string displayName;
    private readonly string caption;
    private readonly Action<string, string> showBalloon;

    // Interlocked-guarded: 1 while a check/install is in flight, so a menu click during the
    // launch check (or a double click) doesn't start a second download.
    private int busy;

    /// <summary>Creates an updater for the running app, whose version comes from the host's entry assembly.</summary>
    /// <param name="options">Which GitHub repository's releases to update from.</param>
    /// <param name="displayName">The app's name, for messages.</param>
    /// <param name="dataFolderName">The app's data folder, where the update is staged.</param>
    /// <param name="showBalloon">Shows a tray balloon (title, text).</param>
    public CompanionUpdater(CompanionUpdateOptions options, string displayName, string dataFolderName, Action<string, string> showBalloon)
    {
        // The host's exe, not this library, carries the version the release workflow stamps.
        var version = AppVersion.FromAssembly(Assembly.GetEntryAssembly() ?? typeof(CompanionUpdater).Assembly);
        checker = new GitHubReleaseUpdateChecker(options.RepoOwner, options.RepoName, version, assetSuffix: options.AssetSuffix);
        installer = new UpdateInstaller(checker, dataFolderName);
        this.displayName = displayName;
        caption = $"{displayName} updates";
        this.showBalloon = showBalloon;
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
    private async Task RunAsync(bool interactive)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            if (interactive)
            {
                MessageBox.Show("An update check is already in progress.", caption);
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
            MessageBox.Show(message, caption);
        }
        return update;
    }

    /// <summary>Asks the user whether to install <paramref name="update"/> now.</summary>
    /// <param name="update">The newer release found by the check.</param>
    /// <returns>True when the user clicked Yes; otherwise false.</returns>
    private bool ConfirmInstall(AvailableUpdate update) => MessageBox.Show(
        $"Version {update.Version} is available (you have {checker.CurrentVersion}).\n\nInstall it now? The app will restart.",
        caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Downloads and verifies <paramref name="update"/>, launches the script that swaps it in, then shuts the app down so the script can replace it.</summary>
    /// <param name="update">The release to install.</param>
    /// <param name="interactive">True when started from the menu, so a failure gets a message box rather than a balloon.</param>
    /// <returns>A task that completes once shutdown is queued, or once a failure has been reported; it never faults.</returns>
    private async Task InstallAsync(AvailableUpdate update, bool interactive)
    {
        showBalloon($"Updating {displayName}", $"Downloading v{update.Version}. The app will restart when it's ready.");
        try
        {
            await installer.PrepareAndLaunchAsync(update);
        }
        catch (Exception ex)
        {
            ReportInstallFailure(update, ex, interactive);
            return;
        }

        // Once idle, so queued work like the activity-id save runs first; the update script
        // waits for this process to exit.
        _ = Application.Current.Dispatcher.InvokeAsync(
            () => Application.Current.Shutdown(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
            showBalloon("Update failed", $"Couldn't install v{update.Version}: {reason}");
            return;
        }

        if (MessageBox.Show($"Couldn't install v{update.Version}:\n{reason}\n\nOpen the download page?",
                caption, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            // GitHubReleaseUpdateChecker only ever returns an https release page URL.
            Process.Start(new ProcessStartInfo(update.ReleasePageUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }
}
