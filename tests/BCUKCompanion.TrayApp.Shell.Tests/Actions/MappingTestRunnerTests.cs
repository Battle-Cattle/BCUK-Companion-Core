using BCUKCompanion.Core.Actions;
using BCUKCompanion.TrayApp.Actions;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Actions;

public sealed class MappingTestRunnerTests
{
    private static MappingTestRunner Runner(EventActionMapping mapping, Func<IEventActionContext>? buildContext = null)
    {
        buildContext ??= () => new EmptyContext();
        return new MappingTestRunner(new EventActionDispatcher(() => [mapping], buildContext), buildContext);
    }

    [Fact]
    public async Task ASuccessfulRun_ReportsEachActionAsOk()
    {
        var mapping = new EventActionMapping("Lights", [new StubAction(kind: "on"), new StubAction(kind: "off")]);

        MappingTestOutcome outcome = await Runner(mapping).RunAsync(mapping);

        Assert.Equal("Test succeeded.", outcome.Status);
        Assert.Equal("on: OK\noff: OK", outcome.Report);
        Assert.Equal("Test results: Lights", outcome.Caption);
    }

    [Fact]
    public async Task AFailedAction_IsReportedAndTheRunCompletesWithErrors()
    {
        var mapping = new EventActionMapping("Lights", [new StubAction(kind: "on"), new StubAction(succeeds: false, kind: "off")]);

        MappingTestOutcome outcome = await Runner(mapping).RunAsync(mapping);

        Assert.Equal("Test completed with errors.", outcome.Status);
        string[] lines = outcome.Report!.Split('\n');
        Assert.Equal("on: OK", lines[0]);
        Assert.StartsWith("off: ", lines[1]);
        Assert.NotEqual("off: OK", lines[1]);
    }

    [Fact]
    public async Task AMappingWithNoActions_SaysNothingWasDispatched()
    {
        var mapping = new EventActionMapping("Lights", []);

        MappingTestOutcome outcome = await Runner(mapping).RunAsync(mapping);

        Assert.Equal(new MappingTestOutcome("Test did not dispatch any actions."), outcome);
    }

    [Fact]
    public async Task AContextThatThrows_IsReportedNotThrown()
    {
        var mapping = new EventActionMapping("Lights", [new StubAction()]);

        MappingTestOutcome outcome = await Runner(mapping, () => throw new InvalidOperationException("No devices")).RunAsync(mapping);

        Assert.Equal("Test failed: No devices", outcome.Status);
        Assert.Null(outcome.Report);
    }
}
