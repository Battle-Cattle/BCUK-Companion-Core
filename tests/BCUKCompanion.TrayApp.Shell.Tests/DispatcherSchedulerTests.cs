using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

// The WPF Dispatcher only exists on Windows, so each test skips elsewhere before any method touching it is compiled.
public sealed class DispatcherSchedulerTests
{
    [SkippableFact]
    public Task RunsWorkOnTheDispatcherThread()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The WPF Dispatcher is only available on Windows.");
        return RunsWorkOnTheDispatcherThreadCore();
    }

    [SkippableFact]
    public Task CompletesOnlyWhenTheWorkCompletes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The WPF Dispatcher is only available on Windows.");
        return CompletesOnlyWhenTheWorkCompletesCore();
    }

    [SkippableFact]
    public Task PropagatesTheWorksException()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The WPF Dispatcher is only available on Windows.");
        return PropagatesTheWorksExceptionCore();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunsWorkOnTheDispatcherThreadCore()
    {
        using var ui = new DispatcherThread();
        int? ranOn = null;

        await DispatcherScheduler.For(ui.Dispatcher)(() =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });

        Assert.Equal(ui.ThreadId, ranOn);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CompletesOnlyWhenTheWorkCompletesCore()
    {
        using var ui = new DispatcherThread();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        bool finished = false;

        Task scheduled = DispatcherScheduler.For(ui.Dispatcher)(async () =>
        {
            started.SetResult();
            await release.Task;
            finished = true;
        });

        // The work is now parked on release, so the scheduled task must still be running.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduled.IsCompleted);

        release.SetResult();
        await scheduled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PropagatesTheWorksExceptionCore()
    {
        using var ui = new DispatcherThread();

        Task scheduled = DispatcherScheduler.For(ui.Dispatcher)(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("boom", ex.Message);
    }

    /// <summary>A dispatcher running on its own STA thread, standing in for the app's UI thread.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread thread;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!; // Set before ready is signalled.
            ThreadId = thread.ManagedThreadId;
        }

        public Dispatcher Dispatcher { get; }

        public int ThreadId { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            thread.Join();
        }
    }
}
