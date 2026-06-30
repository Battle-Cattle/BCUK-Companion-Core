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

    [Fact]
    public void Read_UnknownKind_ThrowsJsonException()
    {
        var registry = new EventActionTypeRegistry();
        var options = CreateOptions(registry);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IEventAction>("""{"kind":"nonexistent"}""", options));
    }

    [Fact]
    public void Read_MissingKindProperty_ThrowsJsonException()
    {
        var registry = new EventActionTypeRegistry();
        var options = CreateOptions(registry);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IEventAction>("""{"DelaySeconds":5}""", options));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"delay\"")]
    [InlineData("42")]
    public void Read_NonObjectJson_ThrowsJsonException(string json)
    {
        var registry = new EventActionTypeRegistry();
        var options = CreateOptions(registry);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IEventAction>(json, options));
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
