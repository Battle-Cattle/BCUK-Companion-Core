using BCUKCompanion.Core.Updates;
using Xunit;

namespace BCUKCompanion.Core.Tests.Updates;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("v1.2.3+abc123", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    public void TryParseTag_ValidTags_ParseToMajorMinorPatch(string tag, string expected)
    {
        Assert.True(AppVersion.TryParseTag(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("v")]
    public void TryParseTag_InvalidTags_Fail(string tag)
    {
        Assert.False(AppVersion.TryParseTag(tag, out _));
    }

    [Theory]
    [InlineData("1.0.0", false, "1.0.1", true)]
    [InlineData("1.0.0", false, "1.0.0", false)]
    [InlineData("1.1.0", false, "1.0.9", false)]
    [InlineData("1.1.0", true, "1.1.0", true)]
    [InlineData("1.1.0", true, "1.0.9", false)]
    public void IsOlderThan_ComparesVersionsAndTreatsPrereleaseAsOlder(
        string current, bool isPrerelease, string release, bool expected)
    {
        var version = new AppVersion(Version.Parse(current), isPrerelease);

        Assert.Equal(expected, version.IsOlderThan(Version.Parse(release)));
    }

    [Fact]
    public void IsOlderThan_IgnoresTheRevisionPart()
    {
        // An assembly version is 1.2.3.0; a parsed tag leaves the revision undefined.
        Assert.False(new AppVersion(new Version(1, 2, 3), false).IsOlderThan(new Version(1, 2, 3, 0)));
    }

    [Fact]
    public void IsReleaseBuild_FalseOnlyForThePlaceholderVersion()
    {
        Assert.False(new AppVersion(new Version(0, 0, 0), false).IsReleaseBuild);
        Assert.True(new AppVersion(new Version(0, 0, 1), false).IsReleaseBuild);
    }

    [Fact]
    public void FromAssembly_NormalizesToThreeParts()
    {
        var version = AppVersion.FromAssembly(typeof(AppVersionTests).Assembly);

        Assert.Equal(3, version.Version.ToString().Split('.').Length);
    }
}
