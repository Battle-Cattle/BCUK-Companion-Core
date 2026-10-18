using System.Text.Json;

namespace BCUKCompanion.Core.Updates;

/// <summary>
/// Removes the files an older, multi-file build of a companion app left in its install folder
/// once a single-file build has been installed over it. The updater copies new files over the old
/// ones but never deletes anything, so after the switch to single-file releases the folder still
/// holds the old build's runtime, assemblies and translation folders.
/// </summary>
/// <remarks>
/// Only files the old build's own <c>&lt;app&gt;.deps.json</c> lists are removed (plus that
/// build's <c>.pdb</c> files, <c>runtimeconfig.json</c> and the deps.json itself), so anything a
/// user dropped into the folder is left alone. The deps.json is deleted last, and only once
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
    public static bool Run(string installDir, string appName)
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
            files.Add(Path.Combine(dir, fileName));
            if (locale is null && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Path.Combine(dir, Path.ChangeExtension(fileName, ".pdb")));
            }
        }

        files.Add(Path.Combine(installDir, $"{appName}.pdb"));
        return files.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Each "runtime"/"native" entry is a file in the install folder and each "resources" entry a
    // file in its locale's subfolder; package paths such as lib/net10.0/Foo.dll are flattened on
    // publish, so only the file name is kept. Names that could point outside the folder are
    // skipped. Null if the file is missing or can't be parsed.
    private static List<(string? Locale, string FileName)>? ReadDepsEntries(string depsFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(depsFile));
            if (!doc.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ObjectProperties(targets)
                .SelectMany(target => ObjectProperties(target.Value))
                .SelectMany(library => ReadLibraryEntries(library.Value))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

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
                if ((kind != "resources" || locale is not null) && IsPlainName(fileName))
                {
                    yield return (locale, fileName);
                }
            }
        }
    }

    // A resource asset's locale folder, or null if it has none that is safe to use.
    private static string? ReadLocale(JsonElement asset) =>
        asset.ValueKind == JsonValueKind.Object
        && asset.TryGetProperty("locale", out var locale)
        && locale.ValueKind == JsonValueKind.String
        && IsPlainName(locale.GetString())
            ? locale.GetString()
            : null;

    private static IEnumerable<JsonProperty> ObjectProperties(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object)
            : [];

    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".."
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

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

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
