using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

public sealed class BotHostChangeTests
{
    private static readonly Uri Current = new("https://bot.example.com");

    [Theory]
    [InlineData("https://bot.example.com")]
    [InlineData("HTTPS://BOT.EXAMPLE.COM")]
    public void TheSameUrl_IsNoChange(string newBotHost)
    {
        Assert.Null(BotHostChange.Compare(Current, newBotHost));
    }

    [Theory]
    [InlineData("https://bot.example.com/companion")]
    [InlineData("https://bot.example.com:443/")]
    public void OnlyAPathChange_KeepsTheSameServer(string newBotHost)
    {
        BotHostChange change = BotHostChange.Compare(Current, newBotHost)!;

        Assert.True(change.SameServer);
        Assert.Equal(new Uri(newBotHost), change.NewHost);
    }

    [Theory]
    [InlineData("https://other.example.com")]
    [InlineData("https://bot.example.com:8443")]
    [InlineData("http://bot.example.com")]
    public void AnotherSchemeHostOrPort_IsAnotherServer(string newBotHost)
    {
        BotHostChange change = BotHostChange.Compare(Current, newBotHost)!;

        Assert.False(change.SameServer);
        Assert.Equal(new Uri(newBotHost), change.NewHost);
    }

    [Fact]
    public void AnInvalidUrl_Throws()
    {
        Assert.Throws<UriFormatException>(() => BotHostChange.Compare(Current, "not a url"));
    }
}
