using System.Net;
using BCUKCompanion.Core.Auth;
using Xunit;

namespace BCUKCompanion.Core.Tests;

public sealed class LoopbackCallbackListenerTests : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly HttpClient browser = new();
    private readonly string callbackUrl;

    public LoopbackCallbackListenerTests()
    {
        int port = LoopbackCallbackListener.StartListenerOnFreePort(listener);
        callbackUrl = $"http://127.0.0.1:{port}/callback/";
    }

    public void Dispose()
    {
        listener.Close();
        browser.Dispose();
    }

    [Fact]
    public void StartListenerOnFreePort_ListensOnTheLoopbackCallbackPath()
    {
        Assert.True(listener.IsListening);
        Assert.Equal(callbackUrl, Assert.Single(listener.Prefixes));
    }

    [Fact]
    public async Task WaitForCallbackAsync_CodeWithExpectedState_ReturnsIt()
    {
        Task<(HttpListenerContext Context, string Code)> wait = LoopbackCallbackListener.WaitForCallbackAsync(
            listener, "s1", TimeSpan.FromSeconds(10), CancellationToken.None);
        Task<HttpResponseMessage> request = browser.GetAsync($"{callbackUrl}?code=abc&state=s1");

        (HttpListenerContext context, string code) = await wait;
        LoopbackCallbackListener.RespondAndClose(context, 200, "<html>ok</html>");

        Assert.Equal("abc", code);
        using HttpResponseMessage response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WaitForCallbackAsync_ErrorWithAnotherState_IsIgnored()
    {
        Task<(HttpListenerContext Context, string Code)> wait = LoopbackCallbackListener.WaitForCallbackAsync(
            listener, "s1", TimeSpan.FromSeconds(10), CancellationToken.None);

        // An error carrying someone else's state can't end this login.
        using (HttpResponseMessage forged = await browser.GetAsync($"{callbackUrl}?error=access_denied&state=other"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        using (HttpResponseMessage noState = await browser.GetAsync($"{callbackUrl}?code=abc"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noState.StatusCode);
        }

        Assert.False(wait.IsCompleted);
        Task<HttpResponseMessage> real = browser.GetAsync($"{callbackUrl}?code=real&state=s1");
        (HttpListenerContext context, string code) = await wait;
        LoopbackCallbackListener.RespondAndClose(context, 200, "ok");
        (await real).Dispose();

        Assert.Equal("real", code);
    }

    [Fact]
    public async Task WaitForCallbackAsync_ErrorWithExpectedState_FailsTheLogin()
    {
        Task<(HttpListenerContext Context, string Code)> wait = LoopbackCallbackListener.WaitForCallbackAsync(
            listener, "s1", TimeSpan.FromSeconds(10), CancellationToken.None);
        Task<HttpResponseMessage> request = browser.GetAsync($"{callbackUrl}?error=access_denied&state=s1");

        var ex = await Assert.ThrowsAsync<CompanionAuthException>(() => wait);

        Assert.Contains("access_denied", ex.Message);
        using HttpResponseMessage response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WaitForCallbackAsync_NoCallbackBeforeTimeout_ThrowsTimeout()
    {
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoopbackCallbackListener.WaitForCallbackAsync(
            listener, "s1", TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Contains("Timed out", ex.Message);
    }

    [Fact]
    public async Task WaitForCallbackAsync_CallerCancels_ThrowsForTheCallersToken()
    {
        using var cts = new CancellationTokenSource();
        Task<(HttpListenerContext Context, string Code)> wait = LoopbackCallbackListener.WaitForCallbackAsync(
            listener, "s1", TimeSpan.FromSeconds(30), cts.Token);

        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }
}
