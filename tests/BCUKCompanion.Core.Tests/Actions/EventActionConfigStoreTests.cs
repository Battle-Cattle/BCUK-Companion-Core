using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

internal sealed record TestConfig
{
    public List<EventActionMapping> Mappings { get; init; } = [];
}

internal sealed class TestConfigStore(string dataFolderName, EventActionTypeRegistry registry)
    : EventActionConfigStore<TestConfig>(dataFolderName, "test-config.json", registry);

public sealed class EventActionConfigStoreTests : IDisposable
{
    private readonly string dataFolderName = $"BCUKCompanion.Core.Tests.{Guid.NewGuid():N}";
    private readonly TestConfigStore store;

    public EventActionConfigStoreTests()
    {
        store = new TestConfigStore(dataFolderName, new EventActionTypeRegistry());
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(store.ConfigFilePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNewConfig()
    {
        var config = store.Load();

        Assert.Empty(config.Mappings);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsMappings()
    {
        var config = new TestConfig { Mappings = [new EventActionMapping("Hydrate!", [new DelayAction { DelaySeconds = 5 }])] };

        store.Save(config);
        var loaded = store.Load();

        var mapping = Assert.Single(loaded.Mappings);
        Assert.Equal("Hydrate!", mapping.RewardTitle);
        var action = Assert.IsType<DelayAction>(Assert.Single(mapping.Actions));
        Assert.Equal(5, action.DelaySeconds);
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsNewConfig()
    {
        var directory = Path.GetDirectoryName(store.ConfigFilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.ConfigFilePath, "{ not valid json");

        var config = store.Load();

        Assert.Empty(config.Mappings);
        Assert.True(File.Exists(store.ConfigFilePath + ".bak"));
    }
}
