using System.Collections.Concurrent;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

public sealed class ActivityCheckpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ActivityCheckpoint checkpoint = new();
    private readonly ConcurrentQueue<long> saved = new();
    private readonly SemaphoreSlim savedSignal = new(0);

    private void Save(long id) => checkpoint.Save(() =>
    {
        saved.Enqueue(id);
        savedSignal.Release();
        return Task.CompletedTask;
    });

    [Fact]
    public async Task WithNothingInFlight_SavesAtOnce()
    {
        Save(1);

        Assert.True(await savedSignal.WaitAsync(Timeout));
        Assert.Equal([1L], saved);
    }

    [Fact]
    public async Task AnUnfinishedHandler_HoldsBackItsSaveAndEveryLaterOne()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checkpoint.Track(first.Task);
        Save(1);
        checkpoint.Track(second.Task);
        Save(2);

        // The second activity finishing first must not let its id past the unfinished first one.
        second.SetResult();
        Assert.False(await savedSignal.WaitAsync(TimeSpan.FromMilliseconds(100)));

        first.SetResult();
        Assert.True(await savedSignal.WaitAsync(Timeout));
        Assert.True(await savedSignal.WaitAsync(Timeout));
        Assert.Equal([1L, 2L], saved);
    }

    [Fact]
    public async Task AHandlerOrSaveThatFails_DoesNotStopLaterSaves()
    {
        checkpoint.Track(Task.FromException(new InvalidOperationException("handler")));
        checkpoint.Save(() => Task.FromException(new InvalidOperationException("save")));
        Save(3);

        Assert.True(await savedSignal.WaitAsync(Timeout));
        Assert.Equal([3L], saved);
    }
}
