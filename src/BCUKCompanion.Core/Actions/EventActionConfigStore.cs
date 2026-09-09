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

    public string ConfigFilePath { get; }

    public TConfig Load()
    {
        lock (syncRoot)
        {
            if (!File.Exists(ConfigFilePath))
            {
                return new TConfig();
            }

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
    }

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
