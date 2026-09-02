using System.Text.Json;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class CompanionActivityEventTests
{
    [Fact]
    public void DeserializesFollowPayload()
    {
        const string json = """{"type":"follow","displayName":"SomeViewer","detail":null,"occurredAt":"2026-06-27T12:34:56.000Z"}""";

        CompanionActivityEvent? activity = JsonSerializer.Deserialize<CompanionActivityEvent>(json);

        Assert.NotNull(activity);
        Assert.Equal("follow", activity!.Type);
        Assert.Equal("SomeViewer", activity.DisplayName);
        Assert.Null(activity.Detail);
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 12, 34, 56, TimeSpan.Zero), activity.OccurredAt);
    }

    [Fact]
    public void DeserializesRaidPayloadWithDetail()
    {
        const string json = """{"type":"raid","displayName":"SomeStreamer","detail":"50 raiders","occurredAt":"2026-01-01T00:00:00Z"}""";

        CompanionActivityEvent? activity = JsonSerializer.Deserialize<CompanionActivityEvent>(json);

        Assert.NotNull(activity);
        Assert.Equal("raid", activity!.Type);
        Assert.Equal("50 raiders", activity.Detail);
    }
}
