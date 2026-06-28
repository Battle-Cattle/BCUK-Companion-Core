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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
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

    private static AppSettings LoadDefaultsFromBundledConfig()
    {
        var settings = new AppSettings();
        try
        {
            string bundledPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(bundledPath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(bundledPath));
                if (document.RootElement.TryGetProperty("botHost", out JsonElement botHostElement))
                {
                    settings.BotHost = botHostElement.GetString() ?? settings.BotHost;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Use the hard-coded default above.
        }

        return settings;
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(AppPaths.SettingsFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}
