using System.Net;
using System.Text.Json;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class AdvancedCommandClientTests
{
    private static CompanionClient CreateClient(FakeHttpMessageHandler handler, string? token = "abc123")
    {
        var tokenStore = new FakeTokenStore();
        if (token is not null)
        {
            tokenStore.Save(token);
        }

        return new CompanionClient(new Uri("https://bot.example.com"), tokenStore, new HttpClient(handler));
    }

    [Fact]
    public async Task GetAdvancedCommandsAsync_ReturnsDeserializedCommands()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"ok":true,"commands":[{"id":7,"name":"Hydrate","trigger":"!hydrate","pointCost":100,"isEnabled":true,"companionEnabled":true,"parameters":{"color":"red"}}]}
            """);
        using CompanionClient client = CreateClient(handler);

        AdvancedCommand command = Assert.Single(await client.GetAdvancedCommandsAsync());

        Assert.Equal(7, command.Id);
        Assert.Equal("Hydrate", command.Name);
        Assert.Equal("!hydrate", command.Trigger);
        Assert.Equal(100, command.PointCost);
        Assert.True(command.CompanionEnabled);
        Assert.Equal("red", command.Parameters!["color"]);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/companion/commands", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetAdvancedCommandsAsync_ThrowsOnOkFalse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":false,"error":"nope"}""");
        using CompanionClient client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<CompanionApiException>(() => client.GetAdvancedCommandsAsync());
        Assert.Equal("nope", ex.Message);
    }

    /// <summary>Both acknowledgement calls, by name, with the route each one posts to.</summary>
    public static TheoryData<string, string> AckCalls => new()
    {
        { nameof(CompanionClient.AckAdvancedCommandAsync), "/api/companion/commands/ack" },
        { nameof(CompanionClient.AckRedemptionAsync), "/api/companion/rewards/ack" },
    };

    private static Task<bool> SendAck(CompanionClient client, string call, string eventId, bool ok, string? error = null) => call switch
    {
        nameof(CompanionClient.AckAdvancedCommandAsync) => client.AckAdvancedCommandAsync(eventId, ok, error),
        _ => client.AckRedemptionAsync(eventId, ok, error),
    };

    [Theory]
    [MemberData(nameof(AckCalls))]
    public async Task Ack_PostsTheAcknowledgementToItsRoute(string call, string path)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"resolved":true}""");
        using CompanionClient client = CreateClient(handler);

        bool resolved = await SendAck(client, call, "e1", ok: false, error: "Lamp offline");

        Assert.True(resolved);
        HttpRequestMessage request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Equal("abc123", request.Headers.Authorization?.Parameter);
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("e1", body.RootElement.GetProperty("eventId").GetString());
        Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Lamp offline", body.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [MemberData(nameof(AckCalls))]
    public async Task Ack_ReportsAnAlreadyResolvedEvent(string call, string path)
    {
        _ = path;
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"resolved":false}""");
        using CompanionClient client = CreateClient(handler);

        Assert.False(await SendAck(client, call, "e1", ok: true));
    }

    [Theory]
    [MemberData(nameof(AckCalls))]
    public async Task Ack_ThrowsOn404AndWithoutAToken(string call, string path)
    {
        _ = path;
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, """{"ok":false,"error":"event_not_found"}""");
        using CompanionClient client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<CompanionApiException>(() => SendAck(client, call, "e1", ok: true));
        Assert.Equal("event_not_found", ex.Message);

        using CompanionClient noToken = CreateClient(handler, token: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAck(noToken, call, "e1", ok: true));
    }
}
