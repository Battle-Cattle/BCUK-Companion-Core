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

    /// <summary>The last request's body, read before the client disposes it (null for no body).</summary>
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body),
        };
    }
}
