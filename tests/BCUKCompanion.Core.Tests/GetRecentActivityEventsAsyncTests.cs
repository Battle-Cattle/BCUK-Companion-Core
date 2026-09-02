using System.Net;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class GetRecentActivityEventsAsyncTests
{
    private static CompanionClient CreateClient(FakeTokenStore tokenStore, FakeHttpMessageHandler handler) =>
        new(new Uri("https://bot.example.com"), tokenStore, new HttpClient(handler));

    [Fact]
    public async Task ThrowsWithoutTokenAndNeverSendsARequest()
    {
        var tokenStore = new FakeTokenStore();
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"events":[]}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRecentActivityEventsAsync());

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ReturnsDeserializedActivityEventsOnSuccess()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"ok":true,"events":[{"type":"follow","displayName":"SomeViewer","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}]}
            """);
        using CompanionClient client = CreateClient(tokenStore, handler);

        IReadOnlyList<CompanionActivityEvent> events = await client.GetRecentActivityEventsAsync();

        CompanionActivityEvent activity = Assert.Single(events);
        Assert.Equal("follow", activity.Type);
        Assert.Equal("SomeViewer", activity.DisplayName);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("abc123", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("/api/companion/events/recent", handler.LastRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ReturnsEmptyListWhenEventsIsEmptyArray()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"events":[]}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        IReadOnlyList<CompanionActivityEvent> events = await client.GetRecentActivityEventsAsync();

        Assert.Empty(events);
    }

    [Fact]
    public async Task ThrowsCompanionAuthExceptionOn401()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("expired-token");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, """{"ok":false,"error":"Unauthorized"}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        CompanionAuthException ex = await Assert.ThrowsAsync<CompanionAuthException>(() => client.GetRecentActivityEventsAsync());

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Unauthorized", ex.Message);
    }

    [Fact]
    public async Task ThrowsCompanionApiExceptionOn500()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, """{"ok":false,"error":"Failed to fetch recent events"}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        CompanionApiException ex = await Assert.ThrowsAsync<CompanionApiException>(() => client.GetRecentActivityEventsAsync());

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Failed to fetch recent events", ex.Message);
    }
}
