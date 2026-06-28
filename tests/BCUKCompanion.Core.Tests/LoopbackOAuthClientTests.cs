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

        Uri loginUrl = client.BuildLoginUrl(redirectUri, "my-state-123");

        Assert.Equal("https", loginUrl.Scheme);
        Assert.Equal("bot.example.com", loginUrl.Host);
        Assert.Equal("/companion/login", loginUrl.AbsolutePath);

        Dictionary<string, string> query = ParseQuery(loginUrl.Query);
        Assert.Equal(redirectUri.ToString(), query["redirect_uri"]);
        Assert.Equal("my-state-123", query["state"]);
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
