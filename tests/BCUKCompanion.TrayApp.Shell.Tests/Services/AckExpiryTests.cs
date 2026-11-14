using BCUKCompanion.Core;
using BCUKCompanion.TrayApp.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

public sealed class AckExpiryTests
{
    private readonly FakeTimeProvider clock = new();

    [Fact]
    public void ANewEvent_IsNotExpired()
    {
        Assert.False(new AckExpiry(clock).Expired);
    }

    [Fact]
    public async Task APingTheBotStillWaitsFor_KeepsItAliveBeyondTheLimit()
    {
        var expiry = new AckExpiry(clock);
        var ping = expiry.Watch(_ => Task.FromResult(true));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await ping(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(expiry.Expired);
    }

    [Fact]
    public async Task APingTheBotNoLongerWaitsFor_ExpiresItAtOnce()
    {
        var expiry = new AckExpiry(clock);

        Assert.False(await expiry.Watch(_ => Task.FromResult(false))(CancellationToken.None));

        Assert.True(expiry.Expired);
    }

    [Theory]
    [InlineData(404)] // A bot without progress pings: it doesn't extend, so its 3-minute timeout stands.
    [InlineData(503)]
    public async Task FailingPings_ExpireItOnceNothingHasBeenConfirmedForTheLimit(int status)
    {
        var expiry = new AckExpiry(clock);
        var ping = expiry.Watch(_ => throw new CompanionApiException("failed", status));

        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<CompanionApiException>(() => ping(CancellationToken.None));
        Assert.False(expiry.Expired);

        clock.Advance(AckExpiry.UnconfirmedLimit - TimeSpan.FromMinutes(1));
        Assert.True(expiry.Expired);
    }
}
