using System.IO;

namespace BCUKCompanion.Core.Actions;

/// <summary>Resolves file names that must stay inside an app's data folder.</summary>
internal static class DataFolderPath
{
    /// <summary>
    /// Joins <paramref name="relativePath"/> onto <paramref name="folder"/>, rejecting a rooted
    /// path or one that climbs out of the folder (e.g. <c>..\other.json</c>).
    /// </summary>
    /// <param name="folder">The data folder's full path.</param>
    /// <param name="relativePath">A file name, or a path under the data folder.</param>
    /// <param name="paramName">The caller's parameter name, for the exception.</param>
    /// <returns>The file's full path, inside <paramref name="folder"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is rooted or resolves outside <paramref name="folder"/>.</exception>
    public static string Resolve(string folder, string relativePath, string paramName)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The path must be relative to the data folder.", paramName);
        }

        var root = Path.GetFullPath(folder);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        // Windows paths are case-insensitive; elsewhere "App" and "app" are different folders.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(prefix, comparison))
        {
            throw new ArgumentException("The path must stay inside the data folder.", paramName);
        }

        return full;
    }
}
