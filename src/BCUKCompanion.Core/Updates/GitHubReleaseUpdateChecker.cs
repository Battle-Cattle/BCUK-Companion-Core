using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCUKCompanion.Core.Updates;

/// <summary>A newer published release, and the assets to install it from.</summary>
/// <param name="Version">The release's major.minor.patch version.</param>
/// <param name="TagName">The release tag, e.g. <c>v1.2.3</c>.</param>
/// <param name="DownloadUrl">The release zip.</param>
/// <param name="ReleasePageUrl">The release's page, for a manual download.</param>
/// <param name="ChecksumUrl">The zip's <c>.sha256</c> file, or null if the release has none.</param>
public sealed record AvailableUpdate(Version Version, string TagName, Uri DownloadUrl, Uri ReleasePageUrl, Uri? ChecksumUrl = null);

/// <summary>
/// Checks a companion app's GitHub Releases for a newer build and downloads it. Releases are
/// expected to follow the shape the shared <c>companion-release.yml</c> workflow publishes: a
/// <c>v1.2.3</c> tag, a <c>&lt;app&gt;-v1.2.3-win-x64.zip</c> asset and, alongside it, a
/// <c>&lt;zip&gt;.sha256</c> file the download is verified against. Platform-agnostic: installing
/// the downloaded zip is up to the host (see <c>BCUKCompanion.TrayApp.Shell</c>).
/// </summary>
public sealed class GitHubReleaseUpdateChecker
{
    /// <summary>The release asset suffix of a Windows x64 build.</summary>
    public const string DefaultAssetSuffix = "-win-x64.zip";

    /// <summary>Appended to a zip asset's name to find its checksum file.</summary>
    public const string ChecksumSuffix = ".sha256";

    private readonly string repoOwner;
    private readonly string repoName;
    private readonly string assetSuffix;
    private readonly HttpClient http;

    /// <param name="repoOwner">The GitHub owner of the app's repository, e.g. <c>Battle-Cattle</c>.</param>
    /// <param name="repoName">The app's repository name.</param>
    /// <param name="currentVersion">The running build's version.</param>
    /// <param name="httpClient">
    /// Used for every request; when null, one is created with the User-Agent and Accept headers
    /// GitHub's API requires.
    /// </param>
    /// <param name="assetSuffix">Which release asset to install.</param>
    public GitHubReleaseUpdateChecker(
        string repoOwner,
        string repoName,
        AppVersion currentVersion,
        HttpClient? httpClient = null,
        string assetSuffix = DefaultAssetSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetSuffix);

        this.repoOwner = repoOwner;
        this.repoName = repoName;
        this.assetSuffix = assetSuffix;
        CurrentVersion = currentVersion;
        http = httpClient ?? CreateHttpClient(repoName, currentVersion);
    }

    public AppVersion CurrentVersion { get; }

    /// <summary>
    /// Returns the latest non-prerelease release if it is newer than <see cref="CurrentVersion"/>
    /// and carries a matching zip, otherwise <c>null</c>. Network and API errors propagate.
    /// </summary>
    public async Task<AvailableUpdate?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var latestReleaseUrl = new Uri($"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest");
        using var response = await http.GetAsync(latestReleaseUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // No published release yet.
            return null;
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        // releases/latest never returns a pre-release, so only the version number matters here.
        if (release?.TagName is null
            || !AppVersion.TryParseTag(release.TagName, out var version)
            || !CurrentVersion.IsOlderThan(version))
        {
            return null;
        }

        var asset = release.Assets?.FirstOrDefault(a => a.Name?.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase) == true);
        if (asset?.Name is null || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl))
        {
            return null;
        }

        var checksumAsset = release.Assets!.FirstOrDefault(a =>
            string.Equals(a.Name, asset.Name + ChecksumSuffix, StringComparison.OrdinalIgnoreCase));
        Uri.TryCreate(checksumAsset?.BrowserDownloadUrl, UriKind.Absolute, out var checksumUrl);

        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releasePageUrl))
        {
            releasePageUrl = new Uri($"https://github.com/{repoOwner}/{repoName}/releases");
        }

        return new AvailableUpdate(version, release.TagName, downloadUrl, releasePageUrl, checksumUrl);
    }

    /// <summary>
    /// Downloads <paramref name="update"/>'s zip to <paramref name="destinationPath"/> and, when
    /// the release publishes a checksum, verifies it — deleting the file and throwing
    /// <see cref="InvalidDataException"/> on a mismatch, so a truncated or corrupted download is
    /// never installed.
    /// </summary>
    public async Task DownloadAsync(AvailableUpdate update, string destinationPath, CancellationToken cancellationToken = default)
    {
        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(destinationPath);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        if (update.ChecksumUrl is null)
        {
            return;
        }

        var checksumText = await http.GetStringAsync(update.ChecksumUrl, cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifySha256Async(destinationPath, checksumText, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless <paramref name="filePath"/>'s SHA-256
    /// matches <paramref name="checksumText"/> — either a bare hex digest or <c>sha256sum</c>
    /// output (<c>&lt;hex&gt;  &lt;file name&gt;</c>).
    /// </summary>
    public static async Task VerifySha256Async(string filePath, string checksumText, CancellationToken cancellationToken = default)
    {
        var expected = checksumText.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The update's checksum file isn't a SHA-256 digest.");
        }

        byte[] actual;
        await using (var file = File.OpenRead(filePath))
        {
            actual = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(Convert.ToHexString(actual), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded update doesn't match its published checksum.");
        }
    }

    private static HttpClient CreateHttpClient(string repoName, AppVersion currentVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            repoName, currentVersion.Version.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
