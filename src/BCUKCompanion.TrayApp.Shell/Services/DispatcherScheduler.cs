using System.Windows.Threading;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>Adapts a WPF <see cref="Dispatcher"/> to the "run on the UI thread" delegate <see cref="BotEventHandler"/> takes.</summary>
internal static class DispatcherScheduler
{
    /// <summary>
    /// Returns a delegate that queues work on <paramref name="dispatcher"/>'s thread. Its task completes when the
    /// work's own task does, and faults with the work's exception.
    /// </summary>
    /// <param name="dispatcher">The dispatcher whose thread runs the work.</param>
    /// <returns>The scheduling delegate.</returns>
    public static Func<Func<Task>, Task> For(Dispatcher dispatcher) => work => dispatcher.InvokeAsync(work).Task.Unwrap();
}
