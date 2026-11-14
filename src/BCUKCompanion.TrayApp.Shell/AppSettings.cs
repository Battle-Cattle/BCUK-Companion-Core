using System.IO;
using System.Text.Json;
using BCUKCompanion.Core;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// User-editable settings (bot host, autostart) persisted as plain JSON —
/// the bearer token is stored separately, encrypted, via <c>DpapiFileTokenStore</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The bot's base URL; must be https. Defaults to the bundled appsettings.json's <c>botHost</c>.</summary>
    public string BotHost { get; set; } = "https://bot.example.com";

    /// <summary>Whether the app starts when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// The newest activity event id already shown, per bot host (see <see cref="ActivityKey"/>).
    /// Seeds the event stream on startup so the connect-time catch-up doesn't re-announce
    /// activity (e.g. the last follower) that a previous run already showed.
    /// </summary>
    public Dictionary<string, long> LastActivityEventIds { get; set; } = new();

    /// <summary>
    /// The <see cref="LastActivityEventIds"/> key for a bot host: its scheme, host and port,
    /// lower-cased. The event endpoints are root-relative, so any path in the configured URL
    /// doesn't change which event stream is read and must not split its saved id. Any user info
    /// in the URL is left out so credentials never end up in settings.json.
    /// </summary>
    /// <param name="botHost">The configured bot URL.</param>
    /// <returns>The lower-cased scheme, host and port, e.g. <c>https://bot.example.com</c> (non-default port included).</returns>
    public static string ActivityKey(Uri botHost) =>
        botHost.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant();

    /// <summary>
    /// Reads settings.json, falling back to the bundled appsettings.json defaults when it's
    /// missing, unreadable or not valid JSON (a corrupt file is first copied to
    /// <c>settings.json.bak</c>). A saved file whose bot host is missing or invalid keeps its
    /// other settings and takes only the bot host from the defaults.
    /// </summary>
    /// <returns>The saved settings, or defaults when they can't be used; never null.</returns>
    public static AppSettings Load()
    {
        // Read only when needed, so an unreadable bundled file can't affect valid saved settings.
        string bundledPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (ReadSavedSettings() is not { } saved)
        {
            return LoadDefaultsFromBundledConfig(bundledPath);
        }

        if (!IsValidBotHost(saved.BotHost))
        {
            saved.BotHost = LoadDefaultsFromBundledConfig(bundledPath).BotHost;
        }

        // An explicit JSON null overrides the initializer; startup reads this map.
        saved.LastActivityEventIds ??= new();
        return saved;
    }

    /// <summary>
    /// Reads and parses settings.json, backing up a file that isn't valid JSON. A file with no
    /// <c>BotHost</c> gets an empty one, so <see cref="Load"/> replaces it with the bundled host
    /// rather than keeping the property's placeholder default.
    /// </summary>
    /// <returns>The parsed settings, or null when the file is missing, can't be read, or isn't a valid settings object.</returns>
    private static AppSettings? ReadSavedSettings()
    {
        string json;
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return null;
            }

            json = File.ReadAllText(AppPaths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable settings file shouldn't stop the app starting.
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            AppSettings? saved = document.Deserialize<AppSettings>();
            if (saved is not null && !document.RootElement.TryGetProperty(nameof(BotHost), out _))
            {
                saved.BotHost = string.Empty;
            }

            return saved;
        }
        catch (JsonException)
        {
            AtomicFile.TryBackUp(AppPaths.SettingsFile);
            return null;
        }
    }

    /// <summary>Whether <paramref name="botHost"/> is an absolute https URL, as the companion API requires.</summary>
    /// <param name="botHost">The bot URL to check, as text.</param>
    /// <returns>True when it parses as an absolute https URL; otherwise false.</returns>
    private static bool IsValidBotHost(string botHost) =>
        Uri.TryCreate(botHost, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>New settings with <see cref="BotHost"/> taken from the bundled appsettings.json, when it holds a valid one.</summary>
    /// <param name="bundledPath">Path to the appsettings.json shipped next to the executable.</param>
    /// <returns>New settings; <see cref="BotHost"/> keeps its hard-coded default if the file is missing, unreadable, not valid JSON, not a JSON object, or has no https <c>botHost</c> string.</returns>
    internal static AppSettings LoadDefaultsFromBundledConfig(string bundledPath)
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(bundledPath)
                && ReadBundledBotHost(File.ReadAllText(bundledPath)) is string bundledBotHost)
            {
                settings.BotHost = bundledBotHost;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Use the hard-coded default above.
        }

        return settings;
    }

    /// <summary>
    /// The <c>botHost</c> from a bundled appsettings.json. The file ships with each app, so a wrong
    /// shape is a packaging mistake; it's treated as "no bot host" rather than failing startup.
    /// </summary>
    /// <param name="json">The bundled file's contents.</param>
    /// <returns>The <c>botHost</c> when the root is an object and it's an https URL string; otherwise null.</returns>
    /// <exception cref="JsonException">The text isn't valid JSON.</exception>
    private static string? ReadBundledBotHost(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("botHost", out JsonElement botHostElement)
            || botHostElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? botHost = botHostElement.GetString();
        return botHost is not null && IsValidBotHost(botHost) ? botHost : null;
    }

    /// <summary>Writes these settings to settings.json atomically (temp file, then swap).</summary>
    public void Save()
    {
        if (!IsValidBotHost(BotHost))
        {
            throw new InvalidOperationException("BotHost must be an absolute https URL before settings can be saved.");
        }

        // Through a temp file that's flushed before it's swapped in, so a crash or power cut
        // mid-save can't leave settings.json truncated or invalid JSON.
        AtomicFile.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
