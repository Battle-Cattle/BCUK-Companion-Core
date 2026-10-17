using BCUKCompanion.Core.Updates;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>
/// Turns on self-updating from a companion app's GitHub Releases (see
/// <see cref="CompanionTrayAppOptions.Updates"/>). Releases must follow the shape the shared
/// <c>companion-release.yml</c> workflow publishes — see <see cref="GitHubReleaseUpdateChecker"/>.
/// </summary>
public sealed class CompanionUpdateOptions
{
    /// <summary>The GitHub owner of the app's repository, e.g. <c>Battle-Cattle</c>.</summary>
    public required string RepoOwner { get; init; }

    /// <summary>The app's repository name.</summary>
    public required string RepoName { get; init; }

    /// <summary>Which release asset to install.</summary>
    public string AssetSuffix { get; init; } = GitHubReleaseUpdateChecker.DefaultAssetSuffix;

    /// <summary>
    /// Whether to check silently at launch and install any newer release automatically. The
    /// "Check for updates..." tray menu item is added either way.
    /// </summary>
    public bool CheckOnLaunch { get; init; } = true;
}
