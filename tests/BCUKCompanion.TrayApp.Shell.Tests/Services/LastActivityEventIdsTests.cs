using BCUKCompanion.Core.Events;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

// Saves go to AppPaths' process-wide settings file.
[Collection(SharedShellState.Name)]
public sealed class LastActivityEventIdsTests : IDisposable
{
    private const string Key = "https://bot.example.com";

    private readonly TempAppFolder folder = new();
    private readonly HttpClient httpClient = new();

    public void Dispose()
    {
        httpClient.Dispose();
        folder.Dispose();
    }

    private CompanionEventStream Stream() => new(httpClient, new Uri(Key));

    [Fact]
    public void Seed_WithASavedId_ResumesFromIt()
    {
        var settings = new AppSettings { LastActivityEventIds = { [Key] = 42 } };
        CompanionEventStream events = Stream();

        new LastActivityEventIds(settings).Seed(events, Key);

        Assert.Equal(42, events.LastActivityEventId);
        Assert.False(events.BaselineOnFirstCatchUp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Seed_WithNothingSaved_OnlyRecordsWhereTheServerIsUpTo(long? saved)
    {
        var settings = new AppSettings();
        if (saved is { } id)
        {
            settings.LastActivityEventIds[Key] = id;
        }

        CompanionEventStream events = Stream();

        new LastActivityEventIds(settings).Seed(events, Key);

        Assert.True(events.BaselineOnFirstCatchUp);
    }

    [Fact]
    public void Save_KeepsTheNewestIdAndWritesTheSettings()
    {
        var tracker = new LastActivityEventIds(new AppSettings());

        tracker.Save(Key, 10);
        tracker.Save(Key, 5);

        Assert.Equal(10, AppSettings.Load().LastActivityEventIds[Key]);
    }

    [Fact]
    public void Save_IgnoresASettingsFileThatCantBeWritten()
    {
        var settings = new AppSettings();
        var tracker = new LastActivityEventIds(settings);
        Directory.CreateDirectory(AppPaths.SettingsFile); // a folder in its place can't be written over

        tracker.Save(Key, 11);

        Assert.Equal(11, settings.LastActivityEventIds[Key]);
    }

    [Fact]
    public void Save_IgnoresSettingsThatCantBeSaved()
    {
        var settings = new AppSettings { BotHost = "http://insecure.example.com" };

        new LastActivityEventIds(settings).Save(Key, 3);

        Assert.Equal(3, settings.LastActivityEventIds[Key]);
        Assert.False(File.Exists(AppPaths.SettingsFile));
    }
}
