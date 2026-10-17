using System.IO;
using System.Text.Json;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// User-editable settings (bot host, autostart) persisted as plain JSON —
/// the bearer token is stored separately, encrypted, via <c>DpapiFileTokenStore</c>.
/// </summary>
public sealed class AppSettings
{
    public string BotHost { get; set; } = "https://bot.example.com";

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// The newest activity event id already shown, per bot host (see <see cref="ActivityKey"/>).
    /// Seeds the event stream on startup so the connect-time catch-up doesn't re-announce
    /// activity (e.g. the last follower) that a previous run already showed.
    /// </summary>
    public Dictionary<string, long> LastActivityEventIds { get; set; } = new();

    /// <summary>The <see cref="LastActivityEventIds"/> key for a bot host: its URL, lower-cased, without a trailing slash.</summary>
    public static string ActivityKey(Uri botHost) => botHost.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// Reads settings.json, falling back to the bundled appsettings.json defaults when it's
    /// missing, unreadable or has an invalid bot host.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null && IsValidBotHost(loaded.BotHost))
                {
                    // An explicit JSON null overrides the initializer; startup reads this map.
                    loaded.LastActivityEventIds ??= new();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Fall through to defaults — a corrupt settings file shouldn't block startup.
        }

        return LoadDefaultsFromBundledConfig();
    }

    /// <summary>Whether <paramref name="botHost"/> is an absolute https URL, as the companion API requires.</summary>
    private static bool IsValidBotHost(string botHost) =>
        Uri.TryCreate(botHost, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static AppSettings LoadDefaultsFromBundledConfig()
    {
        var settings = new AppSettings();
        try
        {
            string bundledPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(bundledPath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(bundledPath));
                if (document.RootElement.TryGetProperty("botHost", out JsonElement botHostElement)
                    && botHostElement.GetString() is string bundledBotHost
                    && IsValidBotHost(bundledBotHost))
                {
                    settings.BotHost = bundledBotHost;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Use the hard-coded default above.
        }

        return settings;
    }

    /// <summary>Writes these settings to settings.json atomically (temp file, then swap).</summary>
    public void Save()
    {
        if (!IsValidBotHost(BotHost))
        {
            throw new InvalidOperationException("BotHost must be an absolute https URL before settings can be saved.");
        }

        string settingsFile = AppPaths.SettingsFile;
        string? directory = Path.GetDirectoryName(settingsFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        // Write to a temp file and swap it in so a crash/power-loss mid-write can't
        // leave settings.json truncated or invalid JSON.
        string tempFile = settingsFile + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, settingsFile, overwrite: true);
    }
}
