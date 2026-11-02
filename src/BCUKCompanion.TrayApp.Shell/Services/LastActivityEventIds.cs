using System.IO;
using BCUKCompanion.Core.Events;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Remembers the newest activity event shown per bot host, in <see cref="AppSettings.LastActivityEventIds"/>,
/// so the connect-time catch-up doesn't re-announce activity a previous run already showed. Kept
/// free of WPF so it can be tested; the tray app decides which thread its saves run on.
/// </summary>
internal sealed class LastActivityEventIds
{
    private readonly AppSettings _settings;

    /// <summary>Creates the tracker over <paramref name="settings"/>.</summary>
    /// <param name="settings">The app's settings, whose <see cref="AppSettings.LastActivityEventIds"/> are read and saved.</param>
    public LastActivityEventIds(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Seeds <paramref name="events"/> with the last activity id shown for its bot host. With
    /// nothing saved yet (first run, or a new host), it's told to only record where the server is up to.
    /// </summary>
    /// <param name="events">A new client's event stream, not yet listening.</param>
    /// <param name="activityKey">The bot host's key, from <see cref="AppSettings.ActivityKey"/>.</param>
    public void Seed(CompanionEventStream events, string activityKey)
    {
        if (_settings.LastActivityEventIds.TryGetValue(activityKey, out long lastActivityEventId) && lastActivityEventId > 0)
        {
            events.LastActivityEventId = lastActivityEventId;
        }
        else
        {
            events.BaselineOnFirstCatchUp = true;
        }
    }

    /// <summary>
    /// Persists the newest activity id shown for a bot host. Best-effort: failing to save only
    /// means one repeated notification on the next launch.
    /// </summary>
    /// <param name="activityKey">The bot host's key, from <see cref="AppSettings.ActivityKey"/>.</param>
    /// <param name="id">The newest activity event id shown; ignored unless it's above the saved one.</param>
    public void Save(string activityKey, long id)
    {
        if (_settings.LastActivityEventIds.TryGetValue(activityKey, out long saved) && saved >= id)
        {
            return;
        }

        _settings.LastActivityEventIds[activityKey] = id;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }
}
