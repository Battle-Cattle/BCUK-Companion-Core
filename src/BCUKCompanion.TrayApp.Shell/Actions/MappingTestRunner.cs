using BCUKCompanion.Core.Actions;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>What a test run of a mapping produced, for <see cref="EventActionMappingsWindow{TConfig}"/> to show.</summary>
/// <param name="Status">The one-line status to show once the run ends.</param>
/// <param name="Report">Each action's description and result, one per line; null when there's nothing to report.</param>
/// <param name="Caption">The caption to show <paramref name="Report"/> under; null when there's no report.</param>
internal sealed record MappingTestOutcome(string Status, string? Report = null, string? Caption = null);

/// <summary>
/// Runs a mapping's actions as if its event had arrived (the window's Test button) and turns the
/// result into a status line and a per-action report. Kept free of WPF so it can be tested.
/// </summary>
internal sealed class MappingTestRunner
{
    private readonly EventActionDispatcher _dispatcher;
    private readonly Func<IEventActionContext> _buildContext;

    /// <summary>Creates the runner over <paramref name="dispatcher"/>.</summary>
    /// <param name="dispatcher">Runs the mapped actions, reading the window's current (possibly unsaved) mappings.</param>
    /// <param name="buildContext">Builds the context the report's action descriptions are written with.</param>
    public MappingTestRunner(EventActionDispatcher dispatcher, Func<IEventActionContext> buildContext)
    {
        _dispatcher = dispatcher;
        _buildContext = buildContext;
    }

    /// <summary>Runs <paramref name="mapping"/>'s actions now and reports each one's result.</summary>
    /// <param name="mapping">The mapping to test.</param>
    /// <returns>A task whose result is the status and report to show; a failure is reported in its status, never thrown.</returns>
    public async Task<MappingTestOutcome> RunAsync(EventActionMapping mapping)
    {
        EventDispatchResult result;
        string report;
        try
        {
            // Guards DispatchAsync, the context builder, and Describe() alike: the window runs
            // this off an async void button-click handler, so any exception escaping here (the
            // mappings and context builders run outside EventActionDispatcher's own per-action
            // try/catch, and a subclass's Describe() override is equally unguarded) would
            // otherwise crash the process instead of just failing this one test run.
            result = await _dispatcher.DispatchAsync(mapping.RewardTitle, mapping.TriggerKind).ConfigureAwait(true);

            if (result.ActionResults.Count == 0)
            {
                return new MappingTestOutcome("Test did not dispatch any actions.");
            }

            var context = _buildContext();
            report = string.Join("\n", result.ActionResults.Select(r =>
                $"{r.Action.Describe(context)}: {(r.Success ? "OK" : r.ErrorMessage ?? "Failed")}"));
        }
        catch (Exception ex)
        {
            return new MappingTestOutcome($"Test failed: {ex.Message}");
        }

        return new MappingTestOutcome(
            result.AllSucceeded ? "Test succeeded." : "Test completed with errors.",
            report,
            $"Test results: {result.RewardTitle}");
    }
}
