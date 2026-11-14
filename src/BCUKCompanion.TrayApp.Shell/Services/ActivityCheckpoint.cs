namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Orders saves of the last-handled activity id after the handling of every activity that came
/// before them. A saved id tells the next launch to skip that activity and everything older on
/// connect, so it must not be saved while an earlier activity's host handler is still running:
/// exiting then would lose that activity for good.
/// </summary>
internal sealed class ActivityCheckpoint
{
    private readonly object _gate = new();

    // Completes once everything tracked or saved so far has finished.
    private Task _tail = Task.CompletedTask;

    /// <summary>Adds an activity's handling, so saves requested after it wait for it to finish.</summary>
    /// <param name="handling">Completes when the activity's handlers have run, successfully or not.</param>
    public void Track(Task handling)
    {
        lock (_gate)
        {
            _tail = Task.WhenAll(_tail, handling).ContinueWith(
                _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Runs <paramref name="save"/> once every activity tracked before this call has been handled.</summary>
    /// <param name="save">Saves the id; returns a task that completes when it has. A failure is ignored, like a skipped save.</param>
    public void Save(Func<Task> save)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(
                _ => save(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .Unwrap()
                .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
