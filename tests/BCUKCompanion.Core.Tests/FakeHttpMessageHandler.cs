using System.Net;

namespace BCUKCompanion.Core.Tests;

/// <summary>
/// A minimal <see cref="HttpMessageHandler"/> stand-in for tests that need to control the
/// response to a single request (status code + body) without standing up a real server.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public HttpRequestMessage? LastRequest { get; private set; }

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body),
        };
        return Task.FromResult(response);
    }
}
