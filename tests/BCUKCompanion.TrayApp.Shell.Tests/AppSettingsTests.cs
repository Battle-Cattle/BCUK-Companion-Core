using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string folderName = $"BCUKCompanion.TrayApp.Shell.Tests.{Guid.NewGuid():N}";

    public AppSettingsTests()
    {
        AppPaths.Configure(folderName);
    }

    public void Dispose()
    {
        string bundled = Path.Combine(Path.GetTempPath(), folderName + ".bundled");
        if (Directory.Exists(bundled))
        {
            Directory.Delete(bundled, recursive: true);
        }

        string? directory = Path.GetDirectoryName(AppPaths.SettingsFile);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteSettingsFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var settings = new AppSettings
        {
            BotHost = "https://bot.example.org",
            StartWithWindows = true,
            LastActivityEventIds = new() { ["https://bot.example.org"] = 42 },
        };

        settings.Save();
        AppSettings loaded = AppSettings.Load();

        Assert.Equal("https://bot.example.org", loaded.BotHost);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal(42, loaded.LastActivityEventIds["https://bot.example.org"]);
        Assert.False(File.Exists(AppPaths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void Save_NonHttpsBotHost_Throws()
    {
        var settings = new AppSettings { BotHost = "http://bot.example.org" };

        Assert.Throws<InvalidOperationException>(settings.Save);
        Assert.False(File.Exists(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "BotHost": "http://insecure.example.org" }""")]
    [InlineData("""{ "BotHost": "not a url" }""")]
    public void Load_UnusableFile_FallsBackToDefaults(string json)
    {
        WriteSettingsFile(json);

        AppSettings loaded = AppSettings.Load();

        Assert.StartsWith("https://", loaded.BotHost);
        Assert.NotEqual("http://insecure.example.org", loaded.BotHost);
        Assert.False(loaded.StartWithWindows);
    }

    [Fact]
    public void Load_ExplicitNullActivityIds_GivesAnEmptyMap()
    {
        WriteSettingsFile("""{ "BotHost": "https://bot.example.org", "LastActivityEventIds": null }""");

        Assert.Empty(AppSettings.Load().LastActivityEventIds);
    }

    [Fact]
    public void Load_NoFile_UsesDefaults()
    {
        AppSettings loaded = AppSettings.Load();

        Assert.StartsWith("https://", loaded.BotHost);
        Assert.Empty(loaded.LastActivityEventIds);
    }

    [Theory]
    [InlineData("https://Bot.Example.org/", "https://bot.example.org")]
    [InlineData("https://bot.example.org/some/path?x=1", "https://bot.example.org")]
    [InlineData("https://bot.example.org:8443", "https://bot.example.org:8443")]
    [InlineData("https://user:secret@bot.example.org", "https://bot.example.org")]
    public void ActivityKey_IsSchemeHostAndPortOnly(string botHost, string expected)
    {
        Assert.Equal(expected, AppSettings.ActivityKey(new Uri(botHost)));
    }

    private string WriteBundledFile(string json)
    {
        string directory = Path.Combine(Path.GetTempPath(), folderName + ".bundled");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"https://bot.example.org\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("""{ "botHost": 42 }""")]
    [InlineData("""{ "botHost": false }""")]
    [InlineData("""{ "botHost": { "url": "https://bot.example.org" } }""")]
    [InlineData("""{ "botHost": ["https://bot.example.org"] }""")]
    [InlineData("""{ "botHost": null }""")]
    [InlineData("""{ }""")]
    [InlineData("""{ "botHost": "not a url" }""")]
    [InlineData("""{ "botHost": "http://insecure.example.org" }""")]
    [InlineData("{ not json")]
    public void LoadDefaultsFromBundledConfig_UnusableShape_KeepsTheHardCodedDefault(string json)
    {
        AppSettings settings = AppSettings.LoadDefaultsFromBundledConfig(WriteBundledFile(json));

        Assert.Equal(new AppSettings().BotHost, settings.BotHost);
    }

    [Fact]
    public void LoadDefaultsFromBundledConfig_HttpsBotHost_OverridesTheDefault()
    {
        AppSettings settings = AppSettings.LoadDefaultsFromBundledConfig(
            WriteBundledFile("""{ "botHost": "https://bundled.example.org" }"""));

        Assert.Equal("https://bundled.example.org", settings.BotHost);
    }

    [Fact]
    public void LoadDefaultsFromBundledConfig_MissingFile_KeepsTheHardCodedDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), folderName + ".missing", "appsettings.json");

        Assert.Equal(new AppSettings().BotHost, AppSettings.LoadDefaultsFromBundledConfig(path).BotHost);
    }
}
