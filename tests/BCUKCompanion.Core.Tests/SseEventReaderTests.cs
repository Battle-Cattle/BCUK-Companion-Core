using BCUKCompanion.Core.Events;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public class SseEventReaderTests
{
    [Fact]
    public async Task IgnoresCommentOnlyKeepaliveLines()
    {
        var reader = new SseEventReader(new StringReader(": ping\n\n: ping\n\n"));

        var events = new List<SseEvent>();
        await foreach (SseEvent e in reader.ReadEventsAsync())
        {
            events.Add(e);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task ParsesSingleLineDataEvent()
    {
        const string payload = "data: {\"type\":\"channel_points_redemption\",\"rewardId\":\"abc\"}\n\n";
        var reader = new SseEventReader(new StringReader(payload));

        var events = new List<SseEvent>();
        await foreach (SseEvent e in reader.ReadEventsAsync())
        {
            events.Add(e);
        }

        Assert.Single(events);
        Assert.Equal("{\"type\":\"channel_points_redemption\",\"rewardId\":\"abc\"}", events[0].Data);
    }

    [Fact]
    public async Task JoinsMultiLineDataWithNewline()
    {
        const string payload = "data: line1\ndata: line2\n\n";
        var reader = new SseEventReader(new StringReader(payload));

        var events = new List<SseEvent>();
        await foreach (SseEvent e in reader.ReadEventsAsync())
        {
            events.Add(e);
        }

        Assert.Single(events);
        Assert.Equal("line1\nline2", events[0].Data);
    }

    [Fact]
    public async Task CapturesEventNameField()
    {
        const string payload = "event: redemption\ndata: payload\n\n";
        var reader = new SseEventReader(new StringReader(payload));

        var events = new List<SseEvent>();
        await foreach (SseEvent e in reader.ReadEventsAsync())
        {
            events.Add(e);
        }

        Assert.Single(events);
        Assert.Equal("redemption", events[0].EventName);
    }

    [Fact]
    public async Task InvokesOnActivityForEveryLineIncludingComments()
    {
        const string payload = ": ping\ndata: x\n\n";
        var reader = new SseEventReader(new StringReader(payload));

        int activityCount = 0;
        await foreach (SseEvent _ in reader.ReadEventsAsync(onActivity: () => activityCount++))
        {
        }

        // ": ping" line, "data: x" line, and the blank terminator line.
        Assert.Equal(3, activityCount);
    }
}
