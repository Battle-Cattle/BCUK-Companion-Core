using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

internal sealed record LegacyMappingsFile
{
    public List<EventActionMapping> Mappings { get; init; } = [];
}

internal sealed record LegacyNamesFile
{
    public List<string> Names { get; init; } = [];
}

internal sealed record MigratingConfig
{
    public List<EventActionMapping> Mappings { get; init; } = [];

    public List<string> Names { get; init; } = [];
}

// Merges two legacy files the way a companion app would, recording what each Read reported.
internal sealed class MigratingConfigStore(string dataFolderName, EventActionTypeRegistry registry)
    : EventActionConfigStore<MigratingConfig>(dataFolderName, "migrating-config.json", registry)
{
    public List<LegacyConfigReadStatus> Statuses { get; } = [];

    protected override MigratingConfig CreateWhenMissing()
    {
        var mappings = new List<EventActionMapping>();
        var names = new List<string>();
        var migration = BeginLegacyMigration();

        Statuses.Add(migration.Read<LegacyMappingsFile>(
            "legacy-mappings.json", legacy => legacy.Mappings is not null, legacy => mappings.AddRange(legacy.Mappings)));
        Statuses.Add(migration.Read<LegacyNamesFile>("legacy-names.json", null, legacy => names.AddRange(legacy.Names)));

        if (!migration.FoundAny)
        {
            return new MigratingConfig();
        }

        var migrated = new MigratingConfig { Mappings = mappings, Names = names };
        if (migration.Succeeded)
        {
            Save(migrated);
        }

        return migrated;
    }
}

public sealed class LegacyConfigMigrationTests : IDisposable
{
    private readonly string dataFolderName = $"BCUKCompanion.Core.Tests.{Guid.NewGuid():N}";
    private readonly MigratingConfigStore store;
    private readonly string directory;

    public LegacyConfigMigrationTests()
    {
        store = new MigratingConfigStore(dataFolderName, new EventActionTypeRegistry());
        directory = Path.GetDirectoryName(store.ConfigFilePath)!;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void WriteLegacy(string fileName, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), json);
    }

    [Fact]
    public void Load_NoLegacyFiles_ReturnsNewConfigWithoutSaving()
    {
        var config = store.Load();

        Assert.Empty(config.Mappings);
        Assert.Empty(config.Names);
        Assert.Equal([LegacyConfigReadStatus.Missing, LegacyConfigReadStatus.Missing], store.Statuses);
        Assert.False(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_ValidLegacyFiles_MergesThemWithTheStoresActionConverterAndSaves()
    {
        WriteLegacy(
            "legacy-mappings.json",
            """{ "Mappings": [ { "RewardTitle": "Hydrate!", "Actions": [ { "kind": "delay", "DelaySeconds": 5 } ] } ] }""");
        WriteLegacy("legacy-names.json", """{ "Names": [ "desk lamp" ] }""");

        var config = store.Load();

        var mapping = Assert.Single(config.Mappings);
        Assert.Equal("Hydrate!", mapping.RewardTitle);
        Assert.Equal(5, Assert.IsType<DelayAction>(Assert.Single(mapping.Actions)).DelaySeconds);
        Assert.Equal(["desk lamp"], config.Names);
        Assert.Equal([LegacyConfigReadStatus.Read, LegacyConfigReadStatus.Read], store.Statuses);
        Assert.True(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_OnlySomeLegacyFilesExist_MigratesThoseAndSaves()
    {
        WriteLegacy("legacy-names.json", """{ "Names": [ "desk lamp" ] }""");

        var config = store.Load();

        Assert.Equal(["desk lamp"], config.Names);
        Assert.Equal([LegacyConfigReadStatus.Missing, LegacyConfigReadStatus.Read], store.Statuses);
        Assert.True(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_CorruptLegacyFile_KeepsTheOtherFilesContentsButDoesNotSave()
    {
        WriteLegacy("legacy-mappings.json", "{ not valid json");
        WriteLegacy("legacy-names.json", """{ "Names": [ "desk lamp" ] }""");

        var config = store.Load();

        Assert.Equal(["desk lamp"], config.Names);
        Assert.Equal([LegacyConfigReadStatus.Failed, LegacyConfigReadStatus.Read], store.Statuses);
        Assert.False(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_AfterAFailedMigration_RetriesOnTheNextLoad()
    {
        WriteLegacy("legacy-mappings.json", "{ not valid json");
        store.Load();

        WriteLegacy("legacy-mappings.json", """{ "Mappings": [ { "RewardTitle": "Fixed", "Actions": [] } ] }""");
        var config = store.Load();

        Assert.Equal("Fixed", Assert.Single(config.Mappings).RewardTitle);
        Assert.True(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_LegacyFileFailingValidation_CountsAsFailed()
    {
        WriteLegacy("legacy-mappings.json", """{ "Mappings": null }""");

        var config = store.Load();

        Assert.Empty(config.Mappings);
        Assert.Equal(LegacyConfigReadStatus.Failed, store.Statuses[0]);
        Assert.False(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_LegacyFileHoldingJsonNull_CountsAsFailed()
    {
        WriteLegacy("legacy-names.json", "null");

        store.Load();

        Assert.Equal(LegacyConfigReadStatus.Failed, store.Statuses[1]);
        Assert.False(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Load_ExistingConfig_DoesNotReadLegacyFiles()
    {
        store.Save(new MigratingConfig { Names = ["current"] });
        WriteLegacy("legacy-names.json", """{ "Names": [ "old" ] }""");

        var config = store.Load();

        Assert.Equal(["current"], config.Names);
        Assert.Empty(store.Statuses);
    }
}
