using System.Reflection;

namespace BCUKCompanion.Core.Updates;

/// <summary>
/// A companion app's own version, as stamped by the release workflow (<c>-p:Version=1.2.3</c>),
/// in the major.minor.patch form <see cref="GitHubReleaseUpdateChecker"/> compares release tags
/// against. Local builds keep the csproj's <c>0.0.0</c> placeholder, which
/// <see cref="IsReleaseBuild"/> uses to keep a developer's build from replacing itself with the
/// published one.
/// </summary>
/// <param name="Version">The major.minor.patch version.</param>
/// <param name="IsPrerelease">True for a build such as v1.1.0-beta.1.</param>
public sealed record AppVersion(Version Version, bool IsPrerelease)
{
    /// <summary>
    /// Reads <paramref name="assembly"/>'s version. Its assembly version is just 1.1.0 for a
    /// v1.1.0-beta.1 build, so the pre-release suffix is read from the informational version (the
    /// full <c>-p:Version</c>, minus any <c>+commit</c>).
    /// </summary>
    /// <param name="assembly">The app's entry assembly, stamped by the release workflow.</param>
    /// <returns>The assembly's major.minor.patch version (0.0.0 when it has none), flagged as a pre-release when the informational version has a <c>-</c> suffix.</returns>
    public static AppVersion FromAssembly(Assembly assembly)
    {
        var version = Normalize(assembly.GetName().Version ?? new Version(0, 0, 0));
        var isPrerelease = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0].Contains('-') == true;
        return new AppVersion(version, isPrerelease);
    }

    /// <summary>False for a local/dev build (version 0.0.0), which must never update itself.</summary>
    public bool IsReleaseBuild => Version > new Version(0, 0, 0);

    /// <summary>
    /// Whether a stable release numbered <paramref name="release"/> should replace this build. A
    /// pre-release build counts as older than the stable release of the same number, so someone on
    /// v1.1.0-beta.1 is offered v1.1.0.
    /// </summary>
    /// <param name="release">The stable release's version; only major.minor.patch is compared.</param>
    /// <returns>True when <paramref name="release"/> is newer, or the same number while this build is a pre-release.</returns>
    public bool IsOlderThan(Version release)
    {
        var normalized = Normalize(release);
        return normalized > Version || (normalized == Version && IsPrerelease);
    }

    /// <summary>Parses a release tag: v1.2.3, v1.2.3-beta.1 and v1.2.3+meta all give 1.2.3.</summary>
    /// <param name="tag">The release's tag name, with or without a leading <c>v</c>.</param>
    /// <param name="version">The parsed major.minor.patch version; 0.0.0 when parsing fails.</param>
    /// <returns>True when the tag holds a version number.</returns>
    public static bool TryParseTag(string tag, out Version version)
    {
        string core = tag.TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(core, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>The version as text, e.g. "1.2.3".</summary>
    /// <returns>The major.minor.patch version, e.g. "1.2.3".</returns>
    public override string ToString() => Version.ToString();

    /// <summary>
    /// Compare on major.minor.patch only: the assembly version carries a 4th (revision) part of 0
    /// while a parsed "1.2.3" leaves it undefined, and those would otherwise compare unequal.
    /// </summary>
    /// <param name="v">The version to normalize; an undefined patch part becomes 0.</param>
    /// <returns>A three-part version with the same major, minor and patch.</returns>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
