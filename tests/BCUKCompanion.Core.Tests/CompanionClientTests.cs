using Xunit;

namespace BCUKCompanion.Core.Tests;

public class CompanionClientTests
{
    private static CompanionClient CreateClient(FakeTokenStore tokenStore) =>
        new(new Uri("https://bot.example.com"), tokenStore, new HttpClient());

    [Fact]
    public void IsLoggedInReflectsTokenStoreState()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);

        Assert.False(client.IsLoggedIn);

        client.SetManualToken("abc123");
        Assert.True(client.IsLoggedIn);
    }

    [Fact]
    public void LogoutClearsToken()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);
        client.SetManualToken("abc123");

        client.Logout();

        Assert.False(client.IsLoggedIn);
        Assert.Null(tokenStore.Load());
    }

    [Fact]
    public void StartListeningWithoutTokenThrows()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);

        Assert.Throws<InvalidOperationException>(() => client.StartListening());
    }

    [Fact]
    public void StopListeningWithoutStartingIsNoOp()
    {
        var tokenStore = new FakeTokenStore();
        using CompanionClient client = CreateClient(tokenStore);

        client.StopListening();
    }
}
