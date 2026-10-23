using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Reads an older install's config files so <see cref="EventActionConfigStore{TConfig}.CreateWhenMissing"/>
/// can merge them into the new config. Files are resolved in the store's data folder and parsed
/// with the store's own serializer options, so their actions read the same way the config's do.
/// Start one with <see cref="EventActionConfigStore{TConfig}.BeginLegacyMigration"/>, call
/// <see cref="Read"/> once per legacy file, and save the merged config only when
/// <see cref="Succeeded"/> is true.
/// </summary>
public sealed class LegacyConfigMigration
{
    private readonly string directory;
    private readonly JsonSerializerOptions serializerOptions;

    /// <summary>Creates a migration reading legacy files from <paramref name="directory"/>.</summary>
    /// <param name="directory">The store's data folder, where the legacy files live.</param>
    /// <param name="serializerOptions">The store's serializer options, which can read its registered action kinds.</param>
    internal LegacyConfigMigration(string directory, JsonSerializerOptions serializerOptions)
    {
        this.directory = directory;
        this.serializerOptions = serializerOptions;
    }

    /// <summary>Whether any legacy file read so far exists, whether or not it could be read. False means a fresh install.</summary>
    public bool FoundAny { get; private set; }

    /// <summary>
    /// Whether every legacy file that exists was read successfully. Save the merged config only
    /// when this is true: once the new config file exists the migration never runs again, so
    /// saving after a failed read would lose that file's settings for good. Leaving the new file
    /// missing lets the next launch retry.
    /// </summary>
    public bool Succeeded { get; private set; } = true;

    /// <summary>
    /// Reads one legacy file and hands its contents to <paramref name="onRead"/>. A file that
    /// can't be read or parsed, deserializes to JSON <c>null</c>, or fails
    /// <paramref name="isValid"/> is logged and counts as failed rather than throwing.
    /// </summary>
    /// <remarks>
    /// Use <paramref name="isValid"/> to reject a file with an explicit JSON <c>null</c> for a
    /// collection the legacy shape initializes: System.Text.Json sets the property to null in that
    /// case, overriding the initializer.
    /// </remarks>
    /// <typeparam name="TLegacy">The legacy file's shape.</typeparam>
    /// <param name="fileName">The legacy file's name in the store's data folder.</param>
    /// <param name="isValid">Checks the parsed file is usable, e.g. that its collections aren't null; <see langword="null"/> accepts any non-null result.</param>
    /// <param name="onRead">Takes the parsed file's contents; runs only when the status is <see cref="LegacyConfigReadStatus.Read"/>.</param>
    /// <returns>Whether the file was missing, read, or failed.</returns>
    public LegacyConfigReadStatus Read<TLegacy>(string fileName, Func<TLegacy, bool>? isValid, Action<TLegacy> onRead)
        where TLegacy : class
    {
        var path = Path.Combine(directory, fileName);
        var json = TryReadText(path, out var missing);
        if (missing)
        {
            return LegacyConfigReadStatus.Missing;
        }

        FoundAny = true;
        var legacy = json is null ? null : TryDeserialize<TLegacy>(json, path);
        if (legacy is null || !PassesValidation(legacy, isValid, path))
        {
            Succeeded = false;
            return LegacyConfigReadStatus.Failed;
        }

        onRead(legacy);
        return LegacyConfigReadStatus.Read;
    }

    /// <summary>Runs <paramref name="isValid"/> on a parsed legacy file, logging when it rejects it.</summary>
    /// <typeparam name="TLegacy">The legacy file's shape.</typeparam>
    /// <param name="legacy">The parsed legacy file.</param>
    /// <param name="isValid">The caller's check; <see langword="null"/> accepts anything.</param>
    /// <param name="path">The legacy file's full path, for the log message.</param>
    /// <returns>True when there's no check or the check accepts <paramref name="legacy"/>.</returns>
    private static bool PassesValidation<TLegacy>(TLegacy legacy, Func<TLegacy, bool>? isValid, string path)
    {
        if (isValid is null || isValid(legacy))
        {
            return true;
        }

        Debug.WriteLine($"Failed to migrate legacy config '{path}': it failed validation.");
        return false;
    }

    /// <summary>
    /// Reads the file at <paramref name="path"/>. Only a file that isn't there counts as missing:
    /// one that exists but can't be read (a permission error, a lock, a folder of that name)
    /// is logged and reported as unreadable, so the migration can't save a merge without it.
    /// </summary>
    /// <param name="path">The legacy file's full path.</param>
    /// <param name="missing">Set to true when the file (or its folder) doesn't exist.</param>
    /// <returns>The file's text; <see langword="null"/> when it's missing or couldn't be read.</returns>
    private static string? TryReadText(string path, out bool missing)
    {
        missing = false;
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            missing = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Failed to migrate legacy config '{path}': {ex}");
            return null;
        }
    }

    /// <summary>Parses a legacy file's text, logging rather than throwing when it isn't valid JSON.</summary>
    /// <typeparam name="TLegacy">The legacy file's shape.</typeparam>
    /// <param name="json">The legacy file's text.</param>
    /// <param name="path">The legacy file's full path, for the log message.</param>
    /// <returns>The parsed file; <see langword="null"/> when it isn't valid JSON or holds JSON <c>null</c>.</returns>
    private TLegacy? TryDeserialize<TLegacy>(string json, string path)
        where TLegacy : class
    {
        try
        {
            var legacy = JsonSerializer.Deserialize<TLegacy>(json, serializerOptions);
            if (legacy is null)
            {
                Debug.WriteLine($"Failed to migrate legacy config '{path}': deserialized to null.");
            }

            return legacy;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Failed to migrate legacy config '{path}': {ex}");
            return null;
        }
    }
}
