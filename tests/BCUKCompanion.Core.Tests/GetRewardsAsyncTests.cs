using System.Net;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class GetRewardsAsyncTests
{
    private static CompanionClient CreateClient(FakeTokenStore tokenStore, FakeHttpMessageHandler handler) =>
        new(new Uri("https://bot.example.com"), tokenStore, new HttpClient(handler));

    [Fact]
    public async Task ThrowsWithoutTokenAndNeverSendsARequest()
    {
        var tokenStore = new FakeTokenStore();
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"rewards":[]}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRewardsAsync());

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ReturnsDeserializedRewardsOnSuccess()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {
              "ok": true,
              "rewards": [
                {
                  "id": "reward-uuid-from-twitch",
                  "title": "Highlight My Message",
                  "prompt": "Type something!",
                  "cost": 500,
                  "backgroundColor": "#ff0000",
                  "isEnabled": true,
                  "isUserInputRequired": false
                }
              ]
            }
            """);
        using CompanionClient client = CreateClient(tokenStore, handler);

        IReadOnlyList<Reward> rewards = await client.GetRewardsAsync();

        Reward reward = Assert.Single(rewards);
        Assert.Equal("reward-uuid-from-twitch", reward.Id);
        Assert.Equal("Highlight My Message", reward.Title);
        Assert.Equal("Type something!", reward.Prompt);
        Assert.Equal(500, reward.Cost);
        Assert.Equal("#ff0000", reward.BackgroundColor);
        Assert.True(reward.IsEnabled);
        Assert.False(reward.IsUserInputRequired);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("abc123", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("/api/companion/rewards", handler.LastRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ReturnsEmptyListWhenRewardsIsEmptyArray()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"rewards":[]}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        IReadOnlyList<Reward> rewards = await client.GetRewardsAsync();

        Assert.Empty(rewards);
    }

    [Fact]
    public async Task IncludesDisabledRewardsUnfiltered()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"ok":true,"rewards":[{"id":"r1","title":"Paused Reward","prompt":"","cost":100,"backgroundColor":"#000000","isEnabled":false,"isUserInputRequired":false}]}
            """);
        using CompanionClient client = CreateClient(tokenStore, handler);

        IReadOnlyList<Reward> rewards = await client.GetRewardsAsync();

        Reward reward = Assert.Single(rewards);
        Assert.False(reward.IsEnabled);
    }

    [Fact]
    public async Task ThrowsCompanionApiExceptionWhenResponseEnvelopeIsNotOk()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":false,"error":"Rewards temporarily unavailable"}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        CompanionApiException ex = await Assert.ThrowsAsync<CompanionApiException>(() => client.GetRewardsAsync());

        Assert.Equal("Rewards temporarily unavailable", ex.Message);
    }

    [Fact]
    public async Task ThrowsCompanionAuthExceptionOn401()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("expired-token");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, """{"ok":false,"error":"Unauthorized"}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        CompanionAuthException ex = await Assert.ThrowsAsync<CompanionAuthException>(() => client.GetRewardsAsync());

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Unauthorized", ex.Message);
    }

    [Fact]
    public async Task ThrowsCompanionApiExceptionOn500()
    {
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc123");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, """{"ok":false,"error":"Failed to fetch rewards"}""");
        using CompanionClient client = CreateClient(tokenStore, handler);

        CompanionApiException ex = await Assert.ThrowsAsync<CompanionApiException>(() => client.GetRewardsAsync());

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Failed to fetch rewards", ex.Message);
    }
}
