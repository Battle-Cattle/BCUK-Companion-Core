using BCUKCompanion.Core.Actions;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>
/// Saves <see cref="EventActionMappingsWindow{TConfig}"/>'s config off the UI thread and words the
/// result as a status line. Kept free of WPF so it can be tested.
/// </summary>
/// <typeparam name="TConfig">The app's config type.</typeparam>
internal sealed class EventActionConfigSaver<TConfig>
    where TConfig : new()
{
    private readonly EventActionConfigStore<TConfig> _configStore;

    /// <summary>Creates the saver for <paramref name="configStore"/>.</summary>
    /// <param name="configStore">Where the config is written.</param>
    public EventActionConfigSaver(EventActionConfigStore<TConfig> configStore)
    {
        _configStore = configStore;
    }

    /// <summary>
    /// Builds the config on the calling thread, then writes it on a pool thread. A failure in
    /// either step is reported in the returned status, never thrown.
    /// </summary>
    /// <param name="buildConfig">Snapshots the config to save; called once, before this method first yields.</param>
    /// <returns>A task whose result is the status line: where the config was saved, or why saving failed.</returns>
    public async Task<string> SaveAsync(Func<TConfig> buildConfig)
    {
        try
        {
            var config = buildConfig();
            await Task.Run(() => _configStore.Save(config)).ConfigureAwait(true);
            return $"Saved to {_configStore.ConfigFilePath}";
        }
        catch (Exception ex)
        {
            return $"Save failed: {ex.Message}";
        }
    }
}
