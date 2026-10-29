using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BCUKCompanion.Core.Events;
using BCUKCompanion.Core.Models;
using Xunit;

namespace BCUKCompanion.Core.Tests.Events;

public sealed class CompanionEventStreamConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Answers the SSE request and the catch-up request with whatever each delegate returns.</summary>
    private sealed class DelegateHandler(
        Func<HttpResponseMessage> stream,
        Func<HttpResponseMessage>? recent = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.AbsolutePath == "/api/companion/events/recent"
                ? (recent ?? (() => Ok("""{"ok":true,"events":[]}""")))()
                : stream());
    }

    /// <summary>A response body that fails when read, as a dropped connection does.</summary>
    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    /// <summary>Runs the stream until its first connection ends (or it stops by itself), returning the states it reported.</summary>
    private static async Task<List<CompanionConnectionState>> RunOnceAsync(CompanionEventStream stream)
    {
        var states = new ConcurrentQueue<CompanionConnectionState>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.ConnectionStateChanged += (_, state) =>
        {
            states.Enqueue(state);
            if (state == CompanionConnectionState.Disconnected)
            {
                ended.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource();
        Task run = stream.RunAsync("token", cts.Token);
        try
        {
            await Task.WhenAny(ended.Task, run).WaitAsync(TestTimeout);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }

        return [.. states];
    }

    private static CompanionEventStream Stream(HttpMessageHandler handler) => new(new HttpClient(handler), new Uri("https://bot.example.com"));

    [Fact]
    public async Task ARejectedToken_StopsTheStream()
    {
        var stream = Stream(new DelegateHandler(() => Status(HttpStatusCode.Unauthorized)));

        using var cts = new CancellationTokenSource(TestTimeout);
        var states = new List<CompanionConnectionState>();
        stream.ConnectionStateChanged += (_, s) => states.Add(s);
        await stream.RunAsync("token", cts.Token);

        Assert.Equal([CompanionConnectionState.Connecting, CompanionConnectionState.AuthenticationFailed], states);
    }

    [Fact]
    public async Task RateLimiting_IsReportedAndRetried()
    {
        var states = await RunOnceAsync(Stream(new DelegateHandler(() => Status((HttpStatusCode)429))));

        Assert.Equal(
            [CompanionConnectionState.Connecting, CompanionConnectionState.RateLimited, CompanionConnectionState.Disconnected],
            states);
    }

    [Fact]
    public async Task AServerError_IsRetriedWithoutConnecting()
    {
        var states = await RunOnceAsync(Stream(new DelegateHandler(() => Status(HttpStatusCode.BadGateway))));

        Assert.Equal([CompanionConnectionState.Connecting, CompanionConnectionState.Disconnected], states);
    }

    [Fact]
    public async Task AFailedConnect_IsRetried()
    {
        var states = await RunOnceAsync(Stream(new DelegateHandler(() => throw new HttpRequestException("no route"))));

        Assert.Equal([CompanionConnectionState.Connecting, CompanionConnectionState.Disconnected], states);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    public async Task AConnectionDroppedMidStream_IsRetried(string failure)
    {
        Exception error = failure == "io" ? new IOException("reset") : new HttpRequestException("reset");
        var stream = Stream(new DelegateHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(error)),
        }));

        var states = await RunOnceAsync(stream);

        Assert.Equal(
            [CompanionConnectionState.Connecting, CompanionConnectionState.Connected, CompanionConnectionState.Disconnected],
            states);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("throws")]
    public async Task AFailedCatchUp_StillReadsLiveEvents(string failure)
    {
        const string sse = """
            data: {"type":"follow","id":5,"displayName":"Live","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}

            """;
        Func<HttpResponseMessage> recent = failure == "status"
            ? () => Status(HttpStatusCode.InternalServerError)
            : () => throw new HttpRequestException("catch-up failed");
        var stream = Stream(new DelegateHandler(() => Ok(sse), recent));
        var received = new List<CompanionActivityEvent>();
        stream.ActivityReceived += (_, e) => received.Add(e);

        await RunOnceAsync(stream);

        Assert.Equal("Live", Assert.Single(received).DisplayName);
    }

    [Fact]
    public async Task FramesWithoutDataOrType_AndNullCatchUpEntries_AreSkipped()
    {
        const string sse = """
            event: ping
            data:

            data: {"no":"type"}

            data: {"type":"follow","id":5,"displayName":"Live","detail":null,"occurredAt":"2026-01-01T00:00:00Z"}

            """;
        var stream = Stream(new DelegateHandler(() => Ok(sse), () => Ok("""{"ok":true,"events":[null]}""")));
        var received = new List<CompanionActivityEvent>();
        stream.ActivityReceived += (_, e) => received.Add(e);

        await RunOnceAsync(stream);

        Assert.Equal("Live", Assert.Single(received).DisplayName);
    }

    [Fact]
    public async Task RemembersOnlyTheNewestCommandIds()
    {
        static string Command(int n) =>
            $$"""data: {"type":"advanced_command","id":"e{{n}}","commandId":7,"commandName":"Hydrate","source":"chat","userLogin":"a","userName":"A","args":"","pointsSpent":0,"parameters":{},"ackRequired":false,"triggeredAt":"2026-01-01T00:00:00Z"}""";

        // 257 distinct ids push the first out of the remembered set, so its repeat is delivered again.
        var sse = new StringBuilder();
        for (var n = 0; n <= 256; n++)
        {
            sse.Append(Command(n)).Append("\n\n");
        }

        sse.Append(Command(0)).Append("\n\n").Append(Command(256)).Append("\n\n");
        var stream = Stream(new DelegateHandler(() => Ok(sse.ToString())));
        var received = new List<string>();
        stream.AdvancedCommandReceived += (_, e) => received.Add(e.Id);

        await RunOnceAsync(stream);

        Assert.Equal(258, received.Count);
        Assert.Equal("e0", received[^1]);
    }
}
