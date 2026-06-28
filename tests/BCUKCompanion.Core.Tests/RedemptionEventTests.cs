using System.Text.Json;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class RedemptionEventTests
{
    [Fact]
    public void DeserializesSampleFromIntegrationGuide()
    {
        const string json = """
            {"type":"channel_points_redemption","rewardId":"r1","rewardTitle":"Hydrate!","userLogin":"someviewer","userName":"SomeViewer","userInput":"","redeemedAt":"2026-06-27T12:34:56.000Z"}
            """;

        RedemptionEvent? redemption = JsonSerializer.Deserialize<RedemptionEvent>(json);

        Assert.NotNull(redemption);
        Assert.Equal("channel_points_redemption", redemption!.Type);
        Assert.Equal("r1", redemption.RewardId);
        Assert.Equal("Hydrate!", redemption.RewardTitle);
        Assert.Equal("someviewer", redemption.UserLogin);
        Assert.Equal("SomeViewer", redemption.UserName);
        Assert.Equal(string.Empty, redemption.UserInput);
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 12, 34, 56, TimeSpan.Zero), redemption.RedeemedAt);
    }

    [Fact]
    public void UserInputDefaultsToNullWhenAbsent()
    {
        const string json = """{"type":"channel_points_redemption","rewardId":"r1","rewardTitle":"t","userLogin":"u","userName":"U","redeemedAt":"2026-01-01T00:00:00Z"}""";

        RedemptionEvent? redemption = JsonSerializer.Deserialize<RedemptionEvent>(json);

        Assert.NotNull(redemption);
        Assert.Null(redemption!.UserInput);
    }
}
