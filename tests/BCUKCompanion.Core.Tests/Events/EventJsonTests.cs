using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests.Events;

public class EventJsonTests
{
    [Fact]
    public void TryDeserialize_ReadsAValidPayload()
    {
        Assert.True(EventJson.TryDeserialize("""{"rewardTitle":"Hydrate!"}""", out RedemptionEvent? redemption));
        Assert.Equal("Hydrate!", redemption.RewardTitle);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"rewardTitle":42}""")]
    public void TryDeserialize_FailsOnMalformedOrNullJson(string json)
    {
        Assert.False(EventJson.TryDeserialize(json, out RedemptionEvent? redemption));
        Assert.Null(redemption);
    }
}
