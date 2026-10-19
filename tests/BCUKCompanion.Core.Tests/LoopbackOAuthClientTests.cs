using System.Net;
using System.Text.Json;
using BCUKCompanion.Core.Auth;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class LoopbackOAuthClientTests
{
    [Fact]
    public void BuildLoginUrlIncludesRedirectUriAndState()
    {
        var client = new LoopbackOAuthClient(new HttpClient(), new Uri("https://bot.example.com"));
        var redirectUri = new Uri("http://127.0.0.1:53127/callback");

        Uri loginUrl = client.BuildLoginUrl(redirectUri, "my-state-123", "challenge-abc");

        Assert.Equal("https", loginUrl.Scheme);
        Assert.Equal("bot.example.com", loginUrl.Host);
        Assert.Equal("/companion/login", loginUrl.AbsolutePath);

        Dictionary<string, string> query = ParseQuery(loginUrl.Query);
        Assert.Equal(redirectUri.ToString(), query["redirect_uri"]);
        Assert.Equal("my-state-123", query["state"]);
        Assert.Equal("challenge-abc", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
    }

    [Fact]
    public void CodeChallengeMatchesTheRfc7636Example()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            LoopbackOAuthClient.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public async Task LoginAsync_IgnoresAStrayCallbackAndSendsTheVerifierForTheChallenge()
    {
        var botHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"token":"issued-token"}""");
        var client = new LoopbackOAuthClient(new HttpClient(botHandler), new Uri("https://bot.example.com"));
        using var browser = new HttpClient();
        HttpStatusCode? strayStatus = null;
        Task? browserTask = null;
        string? challenge = null;

        string token = await client.LoginAsync(loginUrl =>
        {
            Dictionary<string, string> query = ParseQuery(loginUrl.Query);
            challenge = query["code_challenge"];
            string callback = query["redirect_uri"];
            browserTask = Task.Run(async () =>
            {
                // Another local process probing the port, or a browser retry, comes first.
                using HttpResponseMessage stray = await browser.GetAsync($"{callback}?code=forged&state=wrong");
                strayStatus = stray.StatusCode;
                using HttpResponseMessage real = await browser.GetAsync(
                    $"{callback}?code=real-code&state={Uri.EscapeDataString(query["state"])}");
            });
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10));
        await browserTask!;

        Assert.Equal("issued-token", token);
        Assert.Equal(HttpStatusCode.BadRequest, strayStatus);
        using JsonDocument exchange = JsonDocument.Parse(botHandler.LastRequestBody!);
        Assert.Equal("real-code", exchange.RootElement.GetProperty("code").GetString());
        string verifier = exchange.RootElement.GetProperty("code_verifier").GetString()!;
        Assert.Equal(challenge, LoopbackOAuthClient.CodeChallenge(verifier));
    }

    [Fact]
    public async Task LoginAsync_FailsAtOnceOnAnErrorRedirectWithTheExpectedState()
    {
        var client = new LoopbackOAuthClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")), new Uri("https://bot.example.com"));
        using var browser = new HttpClient();
        HttpStatusCode? errorStatus = null;
        Task? browserTask = null;

        var ex = await Assert.ThrowsAsync<CompanionAuthException>(() => client.LoginAsync(loginUrl =>
        {
            Dictionary<string, string> query = ParseQuery(loginUrl.Query);
            browserTask = Task.Run(async () =>
            {
                using HttpResponseMessage response = await browser.GetAsync(
                    $"{query["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(query["state"])}");
                errorStatus = response.StatusCode;
            });
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10)));
        await browserTask!;

        Assert.Contains("access_denied", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, errorStatus);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }

        return result;
    }

    [Fact]
    public void GenerateStateProducesNonEmptyUniqueValues()
    {
        string a = LoopbackOAuthClient.GenerateState();
        string b = LoopbackOAuthClient.GenerateState();

        Assert.NotEmpty(a);
        Assert.NotEqual(a, b);
    }
}
