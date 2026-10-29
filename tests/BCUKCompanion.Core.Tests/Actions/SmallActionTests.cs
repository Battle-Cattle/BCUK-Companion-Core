using System.Text.Json;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Updates;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

public sealed class SmallActionTests
{
    private sealed class EmptyContext : IEventActionContext
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>An action whose Kind is serialized like any other property, unlike the real ones.</summary>
    private sealed class KindfulAction : IEventAction
    {
        public string Kind => "kindful";

        public int Level { get; set; } = 3;

        public string Describe(IEventActionContext context) => Kind;

        public IReadOnlyList<string> Validate(IEventActionContext context) => [];

        public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>Cancels the dispatch from inside the action, as a caller stopping mid-run does.</summary>
    private sealed class CancellingAction(CancellationTokenSource cts) : IEventAction
    {
        public string Kind => "cancelling";

        public string Describe(IEventActionContext context) => Kind;

        public IReadOnlyList<string> Validate(IEventActionContext context) => [];

        public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task UnrecognizedAction_DescribesItselfAndNeverRuns()
    {
        var context = new EmptyContext();

        Assert.Equal("Unrecognized action (wiz.future)", new UnrecognizedEventAction("wiz.future", "{}", "unknown").Describe(context));
        Assert.Equal("Unrecognized action", new UnrecognizedEventAction("", "{}", "no kind").Describe(context));
        Assert.False(await new UnrecognizedEventAction("x", "{}", "r").ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public void DelayAction_DescribesItsDelay()
    {
        Assert.Equal("Delay 7s", new DelayAction { DelaySeconds = 7 }.Describe(new EmptyContext()));
    }

    [Fact]
    public void Registry_ListsWhatsRegistered()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register<KindfulAction>("kindful");

        Assert.Equal(typeof(KindfulAction), registry.RegisteredKinds["kindful"]);
        Assert.Equal(typeof(DelayAction), registry.RegisteredKinds[DelayAction.ActionKind]);
    }

    [Fact]
    public void Converter_WritesKindOnce_EvenWhenTheActionSerializesItsOwn()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register<KindfulAction>("kindful");
        var options = new JsonSerializerOptions { Converters = { new EventActionJsonConverter(registry) } };

        var json = JsonSerializer.Serialize<IEventAction>(new KindfulAction(), options);

        Assert.Equal("""{"kind":"kindful","Level":3}""", json);
    }

    [Fact]
    public async Task Dispatch_CancelledByTheCaller_Throws()
    {
        using var cts = new CancellationTokenSource();
        var dispatcher = new EventActionDispatcher(
            () => [new EventActionMapping("Lights", [new CancellingAction(cts)])], () => new EmptyContext());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync("Lights", cts.Token));
    }

    [Fact]
    public void UpdateChecker_WithoutAClient_CreatesItsOwn()
    {
        var checker = new GitHubReleaseUpdateChecker("owner", "repo", new AppVersion(new Version(1, 2, 3), true));

        Assert.Equal(new Version(1, 2, 3), checker.CurrentVersion.Version);
    }
}
