using System.Diagnostics.CodeAnalysis;
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

    /// <summary>The version of the running app, which releases are compared against.</summary>
    public AppVersion CurrentVersion { get; }

    /// <summary>
    /// Returns the latest non-prerelease release if it is newer than <see cref="CurrentVersion"/>
    /// and carries a matching zip, otherwise <c>null</c>. Network and API errors propagate.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task whose result is the newer release's details, or null when there's no release, it isn't newer, it has no matching zip, or one of its URLs isn't https.</returns>
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
        if (asset?.Name is null || !TryCreateHttpsUri(asset.BrowserDownloadUrl, out var downloadUrl))
        {
            return null;
        }

        var checksumAsset = release.Assets!.FirstOrDefault(a =>
            string.Equals(a.Name, asset.Name + ChecksumSuffix, StringComparison.OrdinalIgnoreCase));
        Uri? checksumUrl = null;
        if (checksumAsset is not null && !TryCreateHttpsUri(checksumAsset.BrowserDownloadUrl, out checksumUrl))
        {
            // A published checksum we can't safely fetch must not turn into an unverified install.
            return null;
        }

        if (!TryCreateHttpsUri(release.HtmlUrl, out var releasePageUrl))
        {
            releasePageUrl = new Uri($"https://github.com/{repoOwner}/{repoName}/releases");
        }

        return new AvailableUpdate(version, release.TagName, downloadUrl, releasePageUrl, checksumUrl);
    }

    /// <summary>
    /// Downloads <paramref name="update"/>'s zip to <paramref name="destinationPath"/> and verifies
    /// it against the release's checksum — throwing <see cref="InvalidDataException"/> on a
    /// mismatch, so a truncated or corrupted download is never installed. A release with no
    /// checksum is refused the same way before anything is downloaded: <c>companion-release.yml</c>
    /// always publishes one, so its absence means a hand-made or broken release. On any failure the
    /// partially written or mismatched file is deleted.
    /// </summary>
    /// <param name="update">The release to download, as returned by <see cref="CheckForUpdateAsync"/>.</param>
    /// <param name="destinationPath">The full path to write the zip to; an existing file is overwritten.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A task that completes once the zip has been downloaded and its checksum verified.</returns>
    public async Task DownloadAsync(AvailableUpdate update, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (update.ChecksumUrl is null)
        {
            throw new InvalidDataException(
                $"Release {update.TagName} has no checksum file, so it can't be verified. Download it manually if you trust it.");
        }

        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var file = File.Create(destinationPath);
            try
            {
                await using (file.ConfigureAwait(false))
                {
                    await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Only reached once File.Create succeeded, so this deletes the partial file this
                // call wrote, never a pre-existing one it failed to open.
                TryDelete(destinationPath);
                throw;
            }
        }

        var checksumText = await http.GetStringAsync(update.ChecksumUrl, cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifySha256Async(destinationPath, checksumText, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    /// <summary>
    /// Release URLs come from the API response and the page URL is later opened with the shell,
    /// so anything that isn't plain https (file:, a UNC path, another scheme) is rejected.
    /// </summary>
    /// <param name="value">The URL text from the API response; may be null.</param>
    /// <param name="uri">The parsed URL when it's an absolute https URL; otherwise null.</param>
    /// <returns>True when <paramref name="value"/> is an absolute https URL.</returns>
    private static bool TryCreateHttpsUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        uri = null;
        return false;
    }

    /// <summary>Best-effort: a failure to clean up must not hide the download or checksum error that caused it.</summary>
    /// <param name="path">The full path of the file to delete; any I/O or permission error is ignored.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless <paramref name="filePath"/>'s SHA-256
    /// matches <paramref name="checksumText"/> — either a bare hex digest or <c>sha256sum</c>
    /// output (<c>&lt;hex&gt;  &lt;file name&gt;</c>).
    /// </summary>
    /// <param name="filePath">The full path of the downloaded file to hash.</param>
    /// <param name="checksumText">The checksum file's contents; only its first whitespace-separated token is used.</param>
    /// <param name="cancellationToken">Cancels hashing the file.</param>
    /// <returns>A task that completes when the file matches; it faults with <see cref="InvalidDataException"/> otherwise.</returns>
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

    /// <summary>Creates the client used when the caller doesn't pass one, with the User-Agent and Accept headers GitHub's API expects.</summary>
    /// <param name="repoName">The repository name, sent as the User-Agent product name.</param>
    /// <param name="currentVersion">The running app's version, sent as the User-Agent product version.</param>
    /// <returns>A new client with a 5-minute timeout, owned by this checker.</returns>
    private static HttpClient CreateHttpClient(string repoName, AppVersion currentVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            repoName, currentVersion.Version.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The parts of a GitHub API release this checker reads.</summary>
    private sealed class GitHubRelease
    {
        /// <summary>The release's tag, e.g. <c>v1.2.3</c>.</summary>
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        /// <summary>The release's page on GitHub.</summary>
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        /// <summary>The files attached to the release.</summary>
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    /// <summary>The parts of a GitHub API release asset this checker reads.</summary>
    private sealed class GitHubAsset
    {
        /// <summary>The asset's file name.</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }
        /// <summary>Where the asset downloads from.</summary>
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
