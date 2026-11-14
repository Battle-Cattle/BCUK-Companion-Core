using BCUKCompanion.Core;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

public sealed class AckExpiryTests
{
    [Fact]
    public async Task APingTheBotStillWaitsFor_LeavesItUnexpired()
    {
        var expiry = new AckExpiry();

        Assert.True(await expiry.Watch(_ => Task.FromResult(true))(CancellationToken.None));

        Assert.False(expiry.Expired);
    }

    [Fact]
    public async Task APingTheBotNoLongerWaitsFor_ExpiresIt()
    {
        var expiry = new AckExpiry();

        Assert.False(await expiry.Watch(_ => Task.FromResult(false))(CancellationToken.None));

        Assert.True(expiry.Expired);
    }

    [Fact]
    public async Task AnUnknownEvent_ExpiresItAndStillThrows()
    {
        var expiry = new AckExpiry();
        var ping = expiry.Watch(_ => throw new CompanionApiException("unknown", 404));

        await Assert.ThrowsAsync<CompanionApiException>(() => ping(CancellationToken.None));

        Assert.True(expiry.Expired);
    }

    [Fact]
    public async Task AnyOtherPingFailure_LeavesItUnexpired()
    {
        var expiry = new AckExpiry();
        var ping = expiry.Watch(_ => throw new CompanionApiException("down", 503));

        await Assert.ThrowsAsync<CompanionApiException>(() => ping(CancellationToken.None));

        Assert.False(expiry.Expired);
    }
}
