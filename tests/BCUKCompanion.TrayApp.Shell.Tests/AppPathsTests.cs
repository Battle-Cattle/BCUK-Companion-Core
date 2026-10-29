using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

[Collection(SharedShellState.Name)]
public sealed class AppPathsTests
{
    [Fact]
    public void Paths_AreUnderTheConfiguredFolder()
    {
        using var folder = new TempAppFolder();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder.Name, "settings.json"),
            AppPaths.SettingsFile);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder.Name, "token.bin"),
            AppPaths.TokenFile);
    }

    [Fact]
    public void Options_HaveSingleAppDefaults()
    {
        var options = new CompanionTrayAppOptions();
        var updates = new BCUKCompanion.TrayApp.Updates.CompanionUpdateOptions { RepoOwner = "owner", RepoName = "repo" };

        Assert.Equal(("BCUKCompanion", "BCUK Companion"), (options.DataFolderName, options.DisplayName));
        Assert.True(updates.CheckOnLaunch);
        Assert.Equal(BCUKCompanion.Core.Updates.GitHubReleaseUpdateChecker.DefaultAssetSuffix, updates.AssetSuffix);
        Assert.Equal("Exit", new TrayMenuItem("Exit", () => { }).Text);
    }
}
