using System.Net;
using BCUKCompanion.Core.Auth;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public sealed class LoginAndLogoutTests
{
    private static readonly Uri BotHost = new("https://bot.example.com");

    /// <summary>Plays the browser: follows the bot's redirect back to the loopback listener with a code and the right state.</summary>
    private sealed class FakeBrowser : IDisposable
    {
        private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false });

        public Task<HttpResponseMessage>? Callback { get; private set; }

        public Task Open(Uri loginUrl)
        {
            var query = System.Web.HttpUtility.ParseQueryString(loginUrl.Query);
            Callback = Task.Run(() => http.GetAsync($"{query["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(query["state"]!)}"));
            return Task.CompletedTask;
        }

        public void Dispose() => http.Dispose();
    }

    /// <summary>Throws from every request, as an unreachable bot does.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"code expired"}""", "code expired")]
    [InlineData(HttpStatusCode.InternalServerError, "not json", "Token exchange failed with status 500.")]
    [InlineData(HttpStatusCode.BadRequest, """{"detail":"no error field"}""", "Token exchange failed with status 400.")]
    [InlineData(HttpStatusCode.OK, """{"notAToken":true}""", "Token exchange response did not contain a token.")]
    public async Task LoginAsync_ReportsAFailedExchange_AndTellsTheBrowser(HttpStatusCode status, string body, string expected)
    {
        var client = new LoopbackOAuthClient(new HttpClient(new FakeHttpMessageHandler(status, body)), BotHost);
        using var browser = new FakeBrowser();

        var ex = await Assert.ThrowsAsync<CompanionAuthException>(() => client.LoginAsync(browser.Open, TimeSpan.FromSeconds(10)));

        Assert.Equal(expected, ex.Message);
        using var page = await browser.Callback!;
        Assert.Equal(HttpStatusCode.BadGateway, page.StatusCode);
    }

    [Fact]
    public async Task LoginWithBrowserAsync_SavesTheIssuedToken()
    {
        var store = new FakeTokenStore();
        using var client = new CompanionClient(
            BotHost, store, new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, """{"token":"issued"}""")));
        using var browser = new FakeBrowser();

        await client.LoginWithBrowserAsync(browser.Open);

        Assert.Equal("issued", store.Load());
        Assert.True(client.IsLoggedIn);
        using var page = await browser.Callback!;
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetManualToken_RejectsABlankToken(string token)
    {
        using var client = new CompanionClient(BotHost, new FakeTokenStore());

        Assert.Throws<ArgumentException>(() => client.SetManualToken(token));
        Assert.False(client.IsLoggedIn);
    }

    [Fact]
    public async Task LogoutAsync_WithAnUnreachableBot_LogsOutButCantConfirm()
    {
        var store = new FakeTokenStore();
        store.Save("token");
        using var client = new CompanionClient(BotHost, store, new HttpClient(new UnreachableHandler()));

        Assert.Equal(LogoutResult.NotConfirmed, await client.LogoutAsync());
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task ApiCalls_RejectAnInvalidJsonResponse()
    {
        var store = new FakeTokenStore();
        store.Save("token");
        using var client = new CompanionClient(BotHost, store, new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "<html>")));

        var ex = await Assert.ThrowsAsync<CompanionApiException>(() => client.GetRewardsAsync());

        Assert.Equal("Fetching rewards response was not valid JSON.", ex.Message);
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("progress")]
    public async Task AckAndProgress_ReportTheBotsError(string route)
    {
        var store = new FakeTokenStore();
        store.Save("token");
        using var client = new CompanionClient(
            BotHost, store, new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":false,"error":"unknown event"}""")));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => route == "ack"
            ? client.AckRedemptionAsync("r-1", ok: true)
            : client.ReportRedemptionProgressAsync("r-1"));

        Assert.Contains("unknown event", ex.Message);
    }

    [Fact]
    public void Dispose_OfAClientThatOwnsItsHttpClient_StopsCleanly()
    {
        var client = new CompanionClient(BotHost, new FakeTokenStore());

        Assert.NotNull(client.Events);
        client.Dispose();
    }
}
