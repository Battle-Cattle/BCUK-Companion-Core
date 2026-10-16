using System.Net;
using System.Security.Cryptography;
using System.Text;
using BCUKCompanion.Core.Updates;
using Xunit;

namespace BCUKCompanion.Core.Tests.Updates;

public sealed class GitHubReleaseUpdateCheckerTests : IDisposable
{
    private static readonly AppVersion V100 = new(new Version(1, 0, 0), false);

    private readonly string tempFile = Path.Combine(Path.GetTempPath(), $"BCUKCompanion.Core.Tests.{Guid.NewGuid():N}.zip");

    public void Dispose()
    {
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }
    }

    private static GitHubReleaseUpdateChecker CreateChecker(HttpMessageHandler handler, AppVersion? current = null) =>
        new("Battle-Cattle", "BCUK-Companion-Test", current ?? V100, new HttpClient(handler));

    private static string ReleaseJson(string tag, params string[] assetNames) =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/Battle-Cattle/BCUK-Companion-Test/releases/tag/{{tag}}",
          "assets": [{{string.Join(",", assetNames.Select(n =>
              $$"""{"name":"{{n}}","browser_download_url":"https://example.test/{{n}}"}"""))}}]
        }
        """;

    [Fact]
    public async Task CheckForUpdateAsync_NewerRelease_ReturnsZipAndChecksumAssets()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            ReleaseJson("v1.1.0", "App-v1.1.0-win-x64.zip.sha256", "App-v1.1.0-win-x64.zip"));

        var update = await CreateChecker(handler).CheckForUpdateAsync();

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 1, 0), update.Version);
        Assert.Equal("v1.1.0", update.TagName);
        Assert.Equal(new Uri("https://example.test/App-v1.1.0-win-x64.zip"), update.DownloadUrl);
        Assert.Equal(new Uri("https://example.test/App-v1.1.0-win-x64.zip.sha256"), update.ChecksumUrl);
        Assert.Equal(
            new Uri("https://api.github.com/repos/Battle-Cattle/BCUK-Companion-Test/releases/latest"),
            handler.LastRequest!.RequestUri);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReleaseWithoutChecksum_ReturnsNullChecksumUrl()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0", "App-v1.1.0-win-x64.zip"));

        var update = await CreateChecker(handler).CheckForUpdateAsync();

        Assert.NotNull(update);
        Assert.Null(update.ChecksumUrl);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("v0.9.0")]
    [InlineData("not-a-version")]
    public async Task CheckForUpdateAsync_NotNewer_ReturnsNull(string tag)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, ReleaseJson(tag, $"App-{tag}-win-x64.zip"));

        Assert.Null(await CreateChecker(handler).CheckForUpdateAsync());
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoMatchingAsset_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, ReleaseJson("v1.1.0", "App-v1.1.0-linux-x64.tar.gz"));

        Assert.Null(await CreateChecker(handler).CheckForUpdateAsync());
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoReleaseYet_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, "{}");

        Assert.Null(await CreateChecker(handler).CheckForUpdateAsync());
    }

    [Fact]
    public async Task CheckForUpdateAsync_ServerError_Throws()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateChecker(handler).CheckForUpdateAsync());
    }

    [Fact]
    public async Task VerifySha256Async_MatchingDigest_Succeeds()
    {
        File.WriteAllText(tempFile, "zip contents");
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zip contents"))).ToLowerInvariant();

        await GitHubReleaseUpdateChecker.VerifySha256Async(tempFile, hex);
        await GitHubReleaseUpdateChecker.VerifySha256Async(tempFile, $"{hex}  App-v1.1.0-win-x64.zip\n");
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not a digest")]
    [InlineData("")]
    public async Task VerifySha256Async_WrongOrMalformedDigest_Throws(string checksum)
    {
        File.WriteAllText(tempFile, "zip contents");

        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubReleaseUpdateChecker.VerifySha256Async(tempFile, checksum));
    }

    [Fact]
    public async Task DownloadAsync_ChecksumMismatch_DeletesFileAndThrows()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            "0000000000000000000000000000000000000000000000000000000000000000");
        var update = new AvailableUpdate(new Version(1, 1, 0), "v1.1.0",
            new Uri("https://example.test/a.zip"), new Uri("https://example.test/"), new Uri("https://example.test/a.zip.sha256"));

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateChecker(handler).DownloadAsync(update, tempFile));
        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public async Task DownloadAsync_NoChecksum_WritesFile()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "zip contents");
        var update = new AvailableUpdate(new Version(1, 1, 0), "v1.1.0",
            new Uri("https://example.test/a.zip"), new Uri("https://example.test/"));

        await CreateChecker(handler).DownloadAsync(update, tempFile);

        Assert.Equal("zip contents", File.ReadAllText(tempFile));
    }
}
