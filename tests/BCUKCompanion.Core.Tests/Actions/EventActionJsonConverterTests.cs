using System.Text.Json;
using System.Text.Json.Serialization;
using BCUKCompanion.Core.Actions;
using Xunit;

namespace BCUKCompanion.Core.Tests.Actions;

internal sealed class TestEchoAction : IEventAction
{
    public const string ActionKind = "test.echo";

    public string Message { get; set; } = string.Empty;

    [JsonIgnore]
    public string Kind => ActionKind;

    public string Describe(IEventActionContext context) => $"Echo: {Message}";

    public IReadOnlyList<string> Validate(IEventActionContext context) => [];

    public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken) => Task.FromResult(true);
}

internal sealed class TestRangeAction : IEventAction
{
    public const string ActionKind = "test.range";

    private int _percent;

    public int Percent
    {
        get => _percent;
        set => _percent = value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Percent must be 0-100.");
    }

    [JsonIgnore]
    public string Kind => ActionKind;

    public string Describe(IEventActionContext context) => $"Range: {Percent}";

    public IReadOnlyList<string> Validate(IEventActionContext context) => [];

    public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken) => Task.FromResult(true);
}

public class EventActionJsonConverterTests
{
    private static JsonSerializerOptions CreateOptions(EventActionTypeRegistry registry) => new()
    {
        Converters = { new EventActionJsonConverter(registry) },
    };

    [Fact]
    public void RoundTrips_DelayAction_ThroughRegistryAndConverter()
    {
        var registry = new EventActionTypeRegistry();
        var options = CreateOptions(registry);
        var mapping = new EventActionMapping("Hydrate!", [new DelayAction { DelaySeconds = 42 }]);

        var json = JsonSerializer.Serialize(mapping, options);
        var roundTripped = JsonSerializer.Deserialize<EventActionMapping>(json, options);

        var action = Assert.IsType<DelayAction>(Assert.Single(roundTripped!.Actions));
        Assert.Equal(42, action.DelaySeconds);
        Assert.Equal("delay", action.Kind);
    }

    [Fact]
    public void RoundTrips_CustomRegisteredActionType()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register<TestEchoAction>(TestEchoAction.ActionKind);
        var options = CreateOptions(registry);
        var mapping = new EventActionMapping("Hydrate!", [new TestEchoAction { Message = "hello" }]);

        var json = JsonSerializer.Serialize(mapping, options);
        var roundTripped = JsonSerializer.Deserialize<EventActionMapping>(json, options);

        var action = Assert.IsType<TestEchoAction>(Assert.Single(roundTripped!.Actions));
        Assert.Equal("hello", action.Message);
    }

    [Fact]
    public void Write_EmitsKindAsFirstLowercaseProperty()
    {
        var registry = new EventActionTypeRegistry();
        var options = CreateOptions(registry);

        var json = JsonSerializer.Serialize<IEventAction>(new DelayAction { DelaySeconds = 5 }, options);
        using var doc = JsonDocument.Parse(json);

        var firstProperty = doc.RootElement.EnumerateObject().First();
        Assert.Equal("kind", firstProperty.Name);
        Assert.Equal("delay", firstProperty.Value.GetString());
    }

    [Fact]
    public void Write_DoesNotDuplicateKindProperty()
    {
        var registry = new EventActionTypeRegistry();
        var options = CreateOptions(registry);

        var json = JsonSerializer.Serialize<IEventAction>(new DelayAction { DelaySeconds = 5 }, options);
        using var doc = JsonDocument.Parse(json);

        var kindPropertyCount = doc.RootElement.EnumerateObject()
            .Count(p => string.Equals(p.Name, "kind", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, kindPropertyCount);
    }

    [Theory]
    [InlineData("""{"kind":"nonexistent","Brightness":40}""", "nonexistent", "Unknown action kind")]
    [InlineData("""{"DelaySeconds":5}""", "", "no \"kind\"")]
    [InlineData("""{"kind":"delay","DelaySeconds":"soon"}""", "delay", "Couldn't read this \"delay\" action")]
    public void Read_ActionItCantRead_ReturnsUnrecognizedActionThatNeverValidates(string json, string kind, string reason)
    {
        var options = CreateOptions(new EventActionTypeRegistry());

        var action = Assert.IsType<UnrecognizedEventAction>(JsonSerializer.Deserialize<IEventAction>(json, options));

        Assert.Equal(kind, action.Kind);
        Assert.Contains(reason, Assert.Single(action.Validate(new NoServiceEventActionContext())));
    }

    [Fact]
    public void Write_UnrecognizedAction_WritesItBackExactlyAsRead()
    {
        var options = CreateOptions(new EventActionTypeRegistry());
        const string json = """{"kind":"wiz.future","DeviceId":"abc","Nested":{"a":[1,2]}}""";

        var action = JsonSerializer.Deserialize<IEventAction>(json, options);
        var written = JsonSerializer.Serialize(action, options);

        Assert.Equal(json, written);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"delay\"")]
    [InlineData("42")]
    public void Read_NonObjectJson_ReturnsUnrecognizedActionWrittenBackAsRead(string json)
    {
        var options = CreateOptions(new EventActionTypeRegistry());

        var action = Assert.IsType<UnrecognizedEventAction>(JsonSerializer.Deserialize<IEventAction>(json, options));

        Assert.Contains("isn't a JSON object", Assert.Single(action.Validate(new NoServiceEventActionContext())));
        Assert.Equal(json, JsonSerializer.Serialize<IEventAction>(action, options));
    }

    [Fact]
    public void Read_ActionWhoseSetterThrows_ReturnsUnrecognizedAction()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register<TestRangeAction>(TestRangeAction.ActionKind);
        var options = CreateOptions(registry);

        var action = Assert.IsType<UnrecognizedEventAction>(
            JsonSerializer.Deserialize<IEventAction>("""{"kind":"test.range","Percent":150}""", options));

        Assert.Equal(TestRangeAction.ActionKind, action.Kind);
        Assert.Contains("Couldn't read this \"test.range\" action", Assert.Single(action.Validate(new NoServiceEventActionContext())));
    }

    [Fact]
    public void Read_MappingWithOneBadAction_KeepsTheOtherActions()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register<TestRangeAction>(TestRangeAction.ActionKind);
        var options = CreateOptions(registry);
        const string json = """{"RewardTitle":"Hydrate!","Actions":["stray",{"kind":"test.range","Percent":-1},{"kind":"delay","DelaySeconds":3}]}""";

        var mapping = JsonSerializer.Deserialize<EventActionMapping>(json, options)!;

        Assert.Equal(3, mapping.Actions.Count);
        Assert.IsType<UnrecognizedEventAction>(mapping.Actions[0]);
        Assert.IsType<UnrecognizedEventAction>(mapping.Actions[1]);
        Assert.Equal(3, Assert.IsType<DelayAction>(mapping.Actions[2]).DelaySeconds);
    }

    [Fact]
    public void RoundTrips_EventActionMappingWithMixedActionKinds()
    {
        var registry = new EventActionTypeRegistry();
        registry.Register<TestEchoAction>(TestEchoAction.ActionKind);
        var options = CreateOptions(registry);

        var mapping = new EventActionMapping(
            "Hydrate!",
            [new DelayAction { DelaySeconds = 2 }, new TestEchoAction { Message = "hi" }]);

        var json = JsonSerializer.Serialize(mapping, options);
        var roundTripped = JsonSerializer.Deserialize<EventActionMapping>(json, options)!;

        Assert.Equal(2, roundTripped.Actions.Count);
        Assert.IsType<DelayAction>(roundTripped.Actions[0]);
        Assert.IsType<TestEchoAction>(roundTripped.Actions[1]);
    }
}
