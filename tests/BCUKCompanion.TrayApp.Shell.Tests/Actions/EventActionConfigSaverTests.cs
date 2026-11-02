using BCUKCompanion.Core.Actions;
using BCUKCompanion.TrayApp.Actions;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Actions;

public sealed class EventActionConfigSaverTests : IDisposable
{
    private readonly TestStore store = new($"BCUKCompanion.TrayApp.Shell.Tests.{Guid.NewGuid():N}");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(store.ConfigFilePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public sealed class TestConfig
    {
        public List<EventActionMapping> Mappings { get; init; } = [];
    }

    private sealed class TestStore(string folderName)
        : EventActionConfigStore<TestConfig>(folderName, "mappings.json", new EventActionTypeRegistry());

    [Fact]
    public async Task SaveAsync_WritesTheConfigAndSaysWhere()
    {
        var saver = new EventActionConfigSaver<TestConfig>(store);

        string status = await saver.SaveAsync(() => new TestConfig { Mappings = [new EventActionMapping("Lights", [])] });

        Assert.Equal($"Saved to {store.ConfigFilePath}", status);
        Assert.Equal("Lights", Assert.Single(store.Load().Mappings).RewardTitle);
    }

    [Fact]
    public async Task SaveAsync_ReportsAFailureInsteadOfThrowing()
    {
        var saver = new EventActionConfigSaver<TestConfig>(store);

        string status = await saver.SaveAsync(() => throw new InvalidOperationException("Bad device"));

        Assert.Equal("Save failed: Bad device", status);
        Assert.False(File.Exists(store.ConfigFilePath));
    }
}
