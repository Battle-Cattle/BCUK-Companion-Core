using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BCUKCompanion.Core.Actions;

/// <summary>
/// Shared load/save/backup-on-corruption logic for a companion app's action-config file(s) —
/// each just a JSON document containing whatever integration-specific state (devices, ...) the
/// app needs plus a list of <see cref="EventActionMapping"/>. Lives in Core so every companion
/// app gets this persistence logic for free instead of reimplementing it per app.
/// </summary>
public abstract class EventActionConfigStore<TConfig> where TConfig : new()
{
    private readonly JsonSerializerOptions serializerOptions;

    // Load() (background dispatch thread) and Save() (UI thread) can be called concurrently;
    // this keeps the read and the temp-file-write-then-move fully serialized within this
    // process so neither observes the other's half-finished file state.
    private readonly object syncRoot = new();

    /// <summary>Creates a store for <c>%APPDATA%\<paramref name="dataFolderName"/>\<paramref name="configFileName"/></c>.</summary>
    /// <param name="dataFolderName">The app's own data folder name (see <c>CompanionTrayAppOptions.DataFolderName</c>).</param>
    /// <param name="configFileName">The config file's name.</param>
    /// <param name="registry">The action kinds the file's mappings can contain.</param>
    protected EventActionConfigStore(string dataFolderName, string configFileName, EventActionTypeRegistry registry)
    {
        ConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), dataFolderName, configFileName);
        serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new EventActionJsonConverter(registry) },
        };
    }

    /// <summary>The full path of the config file.</summary>
    public string ConfigFilePath { get; }

    /// <summary>
    /// Reads the config file: <see cref="CreateWhenMissing"/> supplies the config when there's no
    /// file yet, a corrupt file is backed up to <c>.bak</c> and replaced by a new
    /// <typeparamref name="TConfig"/>, and every result passes through <see cref="OnLoaded"/>. A
    /// single action that can't be read (an unregistered kind, a bad value) doesn't make the file
    /// corrupt: it loads as an <see cref="UnrecognizedEventAction"/>, which is saved back unchanged.
    /// </summary>
    /// <returns>The loaded (or newly created) config, after <see cref="OnLoaded"/>.</returns>
    public TConfig Load()
    {
        lock (syncRoot)
        {
            return OnLoaded(File.Exists(ConfigFilePath) ? ReadConfigFile() : CreateWhenMissing());
        }
    }

    /// <summary>
    /// Supplies the config when the file doesn't exist yet: a new <typeparamref name="TConfig"/>
    /// by default. Override to migrate an older install's settings, calling <see cref="Save"/> to
    /// persist the result so the migration runs at most once. Runs under the store's lock, which
    /// <see cref="Save"/> may re-enter from here.
    /// </summary>
    /// <returns>The config to use in place of the missing file.</returns>
    protected virtual TConfig CreateWhenMissing() => new();

    /// <summary>
    /// Post-processes every loaded config before <see cref="Load"/> returns it — e.g. to drop
    /// entries a hand-edited file left invalid. Returns <paramref name="config"/> unchanged by default.
    /// </summary>
    /// <param name="config">The config just read from the file or supplied by <see cref="CreateWhenMissing"/>.</param>
    /// <returns>The config <see cref="Load"/> should return.</returns>
    protected virtual TConfig OnLoaded(TConfig config) => config;

    /// <summary>Reads and parses the existing file. A file that isn't valid JSON is backed up and replaced by a new <typeparamref name="TConfig"/>; I/O and permission errors propagate.</summary>
    /// <returns>The parsed config; a new <typeparamref name="TConfig"/> when the file is corrupt or holds JSON <c>null</c>.</returns>
    private TConfig ReadConfigFile()
    {
        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize<TConfig>(json, serializerOptions) ?? new TConfig();
        }
        catch (JsonException)
        {
            // Only a genuine deserialization failure means the file is corrupt and safe to
            // reset. A transient sharing violation or permission error (IOException,
            // UnauthorizedAccessException) must propagate instead: treating those the same
            // as corruption would hand the caller an empty TConfig indistinguishable from a
            // real reset, and a subsequent Save() would then overwrite an otherwise-intact
            // file with that empty snapshot.
            TryBackUpCorruptConfig();
            return new TConfig();
        }
    }

    /// <summary>Writes <paramref name="config"/> to the file, through a temp file so a crash mid-write can't leave it half-written.</summary>
    /// <param name="config">The config to save.</param>
    public void Save(TConfig config)
    {
        lock (syncRoot)
        {
            var directory = Path.GetDirectoryName(ConfigFilePath)!;
            Directory.CreateDirectory(directory);

            var tempPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(config, serializerOptions));
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
    }

    /// <summary>Copies the corrupt file to <c>.bak</c> so the user can recover it, logging rather than throwing if that fails.</summary>
    private void TryBackUpCorruptConfig()
    {
        try
        {
            File.Copy(ConfigFilePath, ConfigFilePath + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to back up corrupt config '{ConfigFilePath}': {ex}");
        }
    }
}
