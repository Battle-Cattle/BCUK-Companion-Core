using System.Text.Json;
using System.Text.RegularExpressions;

namespace BCUKCompanion.Core.Updates;

/// <summary>
/// Removes the files an older, multi-file build of a companion app left in its install folder
/// once a single-file build has been installed over it. The updater copies new files over the old
/// ones but never deletes anything, so after the switch to single-file releases the folder still
/// holds the old build's runtime, assemblies and translation folders.
/// </summary>
/// <remarks>
/// Files the old build's own <c>&lt;app&gt;.deps.json</c> lists are removed (plus that build's
/// <c>.pdb</c> files, <c>runtimeconfig.json</c> and the deps.json itself). The deps.json doesn't
/// list the framework's own translations or the versioned <c>mscordaccore_*.dll</c> of a runtime
/// older than the last multi-file build, so those are removed by shape instead: culture-named
/// folders holding nothing but <c>*.resources.dll</c>, and <c>mscordaccore_*.dll</c> files. A
/// single-file build bundles its own satellites, so it never ships either. Anything else a user
/// dropped into the folder is left alone. The deps.json is deleted last, and only once
/// everything else is gone, so a file that is locked this time is retried on the next launch.
/// Hosts must only call this from a single-file build: a multi-file build still needs these files.
/// </remarks>
public static class LegacyInstallCleanup
{
    /// <summary>
    /// Deletes the stale files in <paramref name="installDir"/>. Never throws for a file it can't
    /// delete or a deps.json it can't read; returns false if anything was left behind.
    /// </summary>
    /// <param name="installDir">The folder the running executable is in.</param>
    /// <param name="appName">The executable's name without <c>.exe</c>, e.g. <c>MyApp</c>.</param>
    public static bool Run(string installDir, string appName) =>
        RemoveListedFiles(installDir, appName) & RemoveUnlistedRuntimeFiles(installDir);

    /// <summary>Deletes the files the old build's deps.json lists (see <see cref="GetStaleFiles"/>), then any locale folders left empty.</summary>
    /// <returns>False if anything listed couldn't be deleted.</returns>
    private static bool RemoveListedFiles(string installDir, string appName)
    {
        string depsFile = Path.Combine(installDir, $"{appName}.deps.json");
        if (!File.Exists(depsFile))
        {
            return true;
        }

        // An unreadable deps.json is kept rather than lose the only record of what to remove.
        var entries = ReadDepsEntries(depsFile);
        if (entries is null)
        {
            return false;
        }

        bool allDeleted = true;
        foreach (string file in ExistingStaleFiles(installDir, appName, entries))
        {
            allDeleted &= TryDelete(file);
        }

        foreach (string locale in entries.Select(e => e.Locale).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteIfEmpty(Path.Combine(installDir, locale));
        }

        return allDeleted
            && TryDelete(Path.Combine(installDir, $"{appName}.runtimeconfig.json"))
            && TryDelete(depsFile);
    }

    /// <summary>
    /// The stale files that exist in <paramref name="installDir"/>, excluding the deps.json and
    /// runtimeconfig.json themselves. Empty if there is no readable deps.json.
    /// </summary>
    public static IReadOnlyList<string> GetStaleFiles(string installDir, string appName) =>
        ExistingStaleFiles(installDir, appName, ReadDepsEntries(Path.Combine(installDir, $"{appName}.deps.json")) ?? []);

    /// <summary>The files <paramref name="entries"/> name (plus each DLL's PDB and the app's own PDB) that exist, never the executable or appsettings.json.</summary>
    private static List<string> ExistingStaleFiles(
        string installDir, string appName, IEnumerable<(string? Locale, string FileName)> entries)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locale, fileName) in entries)
        {
            // The executable and its settings are what the new build ships too; never touch them.
            if (fileName.Equals($"{appName}.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string dir = locale is null ? installDir : Path.Combine(installDir, locale);
            if (locale is not null && IsReparsePoint(dir))
            {
                // A junction could lead outside the install folder; leave what it points at alone.
                continue;
            }

            files.Add(Path.Combine(dir, fileName));
            if (locale is null && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Path.Combine(dir, Path.ChangeExtension(fileName, ".pdb")));
            }
        }

        files.Add(Path.Combine(installDir, $"{appName}.pdb"));
        return files.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Each "runtime"/"native" entry is a file in the install folder and each "resources" entry a
    /// file in its locale's subfolder; package paths such as lib/net10.0/Foo.dll are flattened on
    /// publish, so only the file name is kept. Only the target the old build ran with (its
    /// runtimeTarget) is read, and paths that are rooted or contain "." or ".." are skipped. Null
    /// if the file is missing, can't be parsed or has no such target.
    /// </summary>
    private static List<(string? Locale, string FileName)>? ReadDepsEntries(string depsFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(depsFile));
            if (GetActiveTarget(doc.RootElement) is not { } target)
            {
                return null;
            }

            return ObjectProperties(target)
                .SelectMany(library => ReadLibraryEntries(library.Value))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The target named by runtimeTarget.name, or null if the file doesn't have one.</summary>
    private static JsonElement? GetActiveTarget(JsonElement root)
    {
        if (GetObject(root, "runtimeTarget") is not { } runtimeTarget
            || !runtimeTarget.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return GetObject(root, "targets") is { } targets ? GetObject(targets, name.GetString()!) : null;
    }

    /// <summary>The object-valued property <paramref name="propertyName"/> of <paramref name="element"/>, or null if either isn't an object.</summary>
    private static JsonElement? GetObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>One library's runtime, native and resource files, keeping only safe paths and plain file names.</summary>
    private static IEnumerable<(string? Locale, string FileName)> ReadLibraryEntries(JsonElement library)
    {
        foreach (string kind in new[] { "runtime", "native", "resources" })
        {
            if (!library.TryGetProperty(kind, out var assets) || assets.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var asset in assets.EnumerateObject())
            {
                string? locale = kind == "resources" ? ReadLocale(asset.Value) : null;
                string fileName = asset.Name[(asset.Name.LastIndexOfAny(['/', '\\']) + 1)..];
                if ((kind != "resources" || locale is not null) && IsSafeAssetPath(asset.Name) && IsPlainName(fileName))
                {
                    yield return (locale, fileName);
                }
            }
        }
    }

    /// <summary>A resource asset's locale folder, or null if it has none that is safe to use.</summary>
    private static string? ReadLocale(JsonElement asset) =>
        asset.ValueKind == JsonValueKind.Object
        && asset.TryGetProperty("locale", out var locale)
        && locale.ValueKind == JsonValueKind.String
        && IsPlainName(locale.GetString())
            ? locale.GetString()
            : null;

    /// <summary>The object-valued properties of <paramref name="element"/>, or none if it isn't an object.</summary>
    private static IEnumerable<JsonProperty> ObjectProperties(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object)
            : [];

    /// <summary>Whether a deps.json asset path is relative and has no "." or ".." segment, so it can't point outside the install folder.</summary>
    private static bool IsSafeAssetPath(string path) =>
        !Path.IsPathRooted(path)
        && path.Split('/', '\\').All(segment => segment is not "." and not "..");

    // Culture names as .NET names its satellite folders: cs, zh-Hans, pt-BR, sr-Latn-RS.
    private static readonly Regex CultureFolderName = new("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

    /// <summary>Deletes what an older build left that its deps.json doesn't list: an older runtime's <c>mscordaccore_*.dll</c> and the framework's satellite-assembly folders.</summary>
    /// <returns>False if anything couldn't be deleted.</returns>
    private static bool RemoveUnlistedRuntimeFiles(string installDir)
    {
        try
        {
            bool allDeleted = true;
            foreach (string file in Directory.EnumerateFiles(installDir, "mscordaccore_*.dll"))
            {
                allDeleted &= TryDelete(file);
            }

            foreach (string dir in Directory.EnumerateDirectories(installDir).Where(IsSatelliteFolder))
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    allDeleted &= TryDelete(file);
                }

                allDeleted &= TryDeleteIfEmpty(dir);
            }

            return allDeleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A folder only the .NET build output would create: a culture name, no subfolders, and
    /// nothing but satellite assemblies. An empty one counts too, so a folder whose delete failed
    /// after its files went is retried next launch. A junction is never followed.
    /// </summary>
    private static bool IsSatelliteFolder(string dir)
    {
        if (!CultureFolderName.IsMatch(Path.GetFileName(dir)) || IsReparsePoint(dir)
            || Directory.EnumerateDirectories(dir).Any())
        {
            return false;
        }

        var files = Directory.GetFiles(dir);
        return files.All(f => f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="dir"/> is a junction or symbolic link. Treated as one if its attributes can't be read, so it's left alone.</summary>
    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether <paramref name="name"/> is a single file or folder name: not blank, not "." or "..", and free of separators and invalid characters.</summary>
    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".."
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Deletes a file, returning false instead of throwing when it can't.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True if the folder is gone; a folder that still has something in it is left alone.</summary>
    private static bool TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }

            return !Directory.Exists(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
