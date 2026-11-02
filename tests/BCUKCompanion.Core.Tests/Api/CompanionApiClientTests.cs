using System.Net;
using System.Text.Json;
using BCUKCompanion.Core.Api;
using Xunit;

namespace BCUKCompanion.Core.Tests.Api;

public class CompanionApiClientTests
{
    private static readonly Uri BotHost = new("https://bot.example.com");

    private static CompanionApiClient CreateClient(HttpMessageHandler handler, FakeTokenStore tokenStore)
        => new(new HttpClient(handler), BotHost, tokenStore);

    [Fact]
    public async Task EachCall_SendsTheTokenSavedAtTheTimeOfTheCall()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"rewards":[]}""");
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("first");
        CompanionApiClient client = CreateClient(handler, tokenStore);

        await client.GetRewardsAsync(CancellationToken.None);
        Assert.Equal("first", handler.LastRequest!.Headers.Authorization?.Parameter);

        tokenStore.Save("second");
        await client.GetRewardsAsync(CancellationToken.None);
        Assert.Equal("second", handler.LastRequest!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Calls_ThrowWithoutASavedToken()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");
        CompanionApiClient client = CreateClient(handler, new FakeTokenStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReportCommandProgressAsync("e1", CancellationToken.None));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ReportRedemptionProgressAsync_PostsTheEventIdAndReturnsExtended()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true,"extended":true}""");
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("abc");
        CompanionApiClient client = CreateClient(handler, tokenStore);

        Assert.True(await client.ReportRedemptionProgressAsync("r1", CancellationToken.None));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/companion/rewards/progress", handler.LastRequest.RequestUri!.AbsolutePath);
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("r1", body.RootElement.GetProperty("eventId").GetString());
    }

    [Fact]
    public void ParseResponse_ReturnsTheParsedBody()
    {
        AckResponse parsed = CompanionApiClient.ParseResponse<AckResponse>(HttpStatusCode.OK, """{"ok":true,"resolved":true}""", "Acking");

        Assert.True(parsed.Ok);
        Assert.True(parsed.Resolved);
    }

    [Theory]
    [InlineData("""{"error":"Token revoked"}""", "Token revoked")]
    [InlineData("", "The companion token was rejected — log in again.")]
    public void ParseResponse_On401_ThrowsAnAuthException(string body, string expected)
    {
        var ex = Assert.Throws<CompanionAuthException>(
            () => CompanionApiClient.ParseResponse<AckResponse>(HttpStatusCode.Unauthorized, body, "Acking"));

        Assert.Equal(expected, ex.Message);
        Assert.Equal(401, ex.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":"No such route"}""", "No such route")]
    [InlineData(HttpStatusCode.InternalServerError, "oops", "Acking failed with status 500.")]
    [InlineData(HttpStatusCode.OK, "not json", "Acking response was not valid JSON.")]
    [InlineData(HttpStatusCode.OK, """{"ok":false,"error":"Unknown event"}""", "Unknown event")]
    [InlineData(HttpStatusCode.OK, """{"ok":false}""", "Acking failed.")]
    [InlineData(HttpStatusCode.OK, "null", "Acking failed.")]
    public void ParseResponse_ThrowsAnApiExceptionForAFailedResponse(HttpStatusCode status, string body, string expected)
    {
        var ex = Assert.Throws<CompanionApiException>(
            () => CompanionApiClient.ParseResponse<AckResponse>(status, body, "Acking"));

        Assert.Equal(expected, ex.Message);
        Assert.Equal((int)status, ex.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, LogoutResult.Revoked)]
    [InlineData(HttpStatusCode.Unauthorized, LogoutResult.Revoked)]
    [InlineData(HttpStatusCode.NotFound, LogoutResult.NotConfirmed)]
    public async Task RevokeTokenAsync_SendsTheGivenTokenAndReportsTheOutcome(HttpStatusCode status, LogoutResult expected)
    {
        var handler = new FakeHttpMessageHandler(status, "{}");
        var tokenStore = new FakeTokenStore();
        tokenStore.Save("saved-token");
        CompanionApiClient client = CreateClient(handler, tokenStore);

        LogoutResult result = await client.RevokeTokenAsync("old-token", CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/companion/token/revoke", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("old-token", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task RevokeTokenAsync_WhenTheRequestFails_ReportsNotConfirmed()
    {
        CompanionApiClient client = CreateClient(new ThrowingHandler(), new FakeTokenStore());

        Assert.Equal(LogoutResult.NotConfirmed, await client.RevokeTokenAsync("t", CancellationToken.None));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
