using BCUKCompanion.Core;
using BCUKCompanion.Core.Tokens;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

public sealed class LoginCleanupTests
{
    private readonly List<string> balloons = [];

    /// <summary>A token store whose saved token can't be deleted.</summary>
    private sealed class UndeletableTokenStore(string token) : ITokenStore
    {
        public string? Load() => token;

        public void Save(string newToken) => token = newToken;

        public void Clear() => throw new UnauthorizedAccessException("Access denied");
    }

    /// <summary>Holds every request open until it's canceled, signalling when the first arrives and when one is canceled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestArrived.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                RequestCanceled.TrySetResult();
            }

            throw new InvalidOperationException("Unreachable");
        }
    }

    private static CompanionClient Client(ITokenStore store, HttpMessageHandler? handler = null) =>
        new(new Uri("https://bot.example.com"), store, new HttpClient(handler ?? new RecordingHttpHandler()));

    /// <summary>Starts listening and waits until the listen loop is connecting with the saved token.</summary>
    private static async Task<CompanionClient> ListeningClient(ITokenStore store, HangingHandler handler)
    {
        CompanionClient client = Client(store, handler);
        client.StartListening();
        await handler.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return client;
    }

    [Fact]
    public void DiscardRejectedLogin_WithNoListenLoopToken_KeepsTheSavedLogin()
    {
        var store = new MemoryTokenStore("newer");
        using CompanionClient client = Client(store);

        Assert.False(LoginCleanup.DiscardRejectedLogin(client, balloons.Add));

        Assert.Equal("newer", store.Load());
        Assert.Empty(balloons);
    }

    [Fact]
    public async Task DiscardRejectedLogin_DiscardsTheTokenTheLoopWasRejectedWith()
    {
        var store = new MemoryTokenStore("rejected");
        var handler = new HangingHandler();
        using CompanionClient client = await ListeningClient(store, handler);

        Assert.True(LoginCleanup.DiscardRejectedLogin(client, balloons.Add));

        Assert.Null(store.Load());
        Assert.Empty(balloons);
        await handler.RequestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)); // and listening stopped
    }

    [Fact]
    public async Task DiscardRejectedLogin_ReportsATokenThatCantBeDeleted()
    {
        var handler = new HangingHandler();
        using CompanionClient client = await ListeningClient(new UndeletableTokenStore("rejected"), handler);

        Assert.True(LoginCleanup.DiscardRejectedLogin(client, balloons.Add));

        Assert.Equal("Couldn't discard the rejected login: Access denied", Assert.Single(balloons));
        await handler.RequestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)); // listening was stopped
    }

    [Theory]
    [InlineData(LogoutResult.Revoked, false)]
    [InlineData(LogoutResult.NoToken, false)]
    [InlineData(LogoutResult.NotConfirmed, true)]
    public async Task FinishOldServerLogoutAsync_SaysSoOnlyWhenTheRevokeWasntConfirmed(LogoutResult result, bool expectBalloon)
    {
        var handler = new HangingHandler();
        CompanionClient client = await ListeningClient(new MemoryTokenStore("token"), handler);

        await LoginCleanup.FinishOldServerLogoutAsync(client, Task.FromResult(result), balloons.Add);

        Assert.Equal(expectBalloon ? [LoginCleanup.OldLoginNotRevokedMessage] : [], balloons);
        await handler.RequestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)); // disposed, which stops listening
    }

    [Fact]
    public async Task FinishOldServerLogoutAsync_DisposesTheClientEvenWhenTheRevokeThrows()
    {
        var handler = new HangingHandler();
        CompanionClient client = await ListeningClient(new MemoryTokenStore("token"), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LoginCleanup.FinishOldServerLogoutAsync(client, Task.FromException<LogoutResult>(new InvalidOperationException()), balloons.Add));

        Assert.Empty(balloons);
        await handler.RequestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)); // disposed, which stops listening
    }
}
